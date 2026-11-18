const FORMATTERS = {
  bold: { delimiters: ['**', '__'], recommended: '**' },
  italic: { delimiters: ['*', '_'], recommended: '_' },
};

function planFormattingEdit(text, start, end, style) {
  const formatter = FORMATTERS[style];
  if (!formatter) throw new RangeError(`Unknown Talemark formatting style: ${style}`);
  if (!Number.isInteger(start) || !Number.isInteger(end) || start < 0 || end > text.length || start > end) {
    throw new RangeError('Formatting selection is outside the document.');
  }
  if (start === end) return null;

  const selectedText = text.slice(start, end);
  for (const delimiter of formatter.delimiters) {
    const openingStart = start - delimiter.length;
    const closingEnd = end + delimiter.length;
    if (openingStart >= 0 && text.slice(openingStart, start) === delimiter
        && text.slice(end, closingEnd) === delimiter
        && isCompletePair(text, openingStart, start, end, closingEnd, delimiter)) {
      return {
        start: openingStart,
        end: closingEnd,
        replacement: selectedText,
        selectionOffset: 0,
        selectedText,
      };
    }
  }

  const enclosing = findEnclosingFormatting(text, start, end, formatter);
  if (enclosing) return enclosing;

  for (const delimiter of formatter.delimiters) {
    const innerStart = start + delimiter.length;
    const innerEnd = end - delimiter.length;
    if (innerStart < innerEnd
        && text.slice(start, innerStart) === delimiter
        && text.slice(innerEnd, end) === delimiter
        && isCompletePair(text, start, innerStart, innerEnd, end, delimiter)) {
      const innerText = text.slice(innerStart, innerEnd);
      return {
        start,
        end,
        replacement: innerText,
        selectionOffset: 0,
        selectedText: innerText,
      };
    }
  }

  const delimiter = formatter.recommended;
  return {
    start,
    end,
    replacement: `${delimiter}${selectedText}${delimiter}`,
    selectionOffset: delimiter.length,
    selectedText,
  };
}

function findEnclosingFormatting(text, start, end, formatter) {
  const opening = readDelimitersBefore(text, start);
  const closing = readDelimitersAfter(text, end);
  if (opening.length === 0 || opening.length !== closing.length
      || opening.some((item, index) => item.delimiter !== closing[closing.length - 1 - index].delimiter)) {
    return null;
  }

  const styleDelimiter = opening.findLastIndex(item => formatter.delimiters.includes(item.delimiter));
  if (styleDelimiter < 0) return null;

  const openingDelimiter = opening[styleDelimiter];
  const closingDelimiter = closing[closing.length - 1 - styleDelimiter];
  const editStart = opening[0].start;
  const editEnd = closing[closing.length - 1].end;
  return {
    start: editStart,
    end: editEnd,
    replacement: text.slice(editStart, openingDelimiter.start)
      + text.slice(openingDelimiter.end, closingDelimiter.start)
      + text.slice(closingDelimiter.end, editEnd),
    selectionOffset: start - editStart - openingDelimiter.delimiter.length,
    selectedText: text.slice(start, end),
  };
}

function readDelimitersBefore(text, offset) {
  const delimiters = [];
  let cursor = offset;
  while (cursor > 0) {
    const marker = text[cursor - 1];
    if (marker !== '*' && marker !== '_') break;
    let runStart = cursor - 1;
    while (runStart > 0 && text[runStart - 1] === marker) runStart--;
    const length = cursor - runStart;
    if ((length !== 1 && length !== 2) || text[cursor] === marker) break;
    delimiters.push({ start: runStart, end: cursor, delimiter: marker.repeat(length) });
    cursor = runStart;
  }
  return delimiters.reverse();
}

function readDelimitersAfter(text, offset) {
  const delimiters = [];
  let cursor = offset;
  while (cursor < text.length) {
    const marker = text[cursor];
    if (marker !== '*' && marker !== '_') break;
    let runEnd = cursor + 1;
    while (runEnd < text.length && text[runEnd] === marker) runEnd++;
    const length = runEnd - cursor;
    if ((length !== 1 && length !== 2) || text[cursor - 1] === marker) break;
    delimiters.push({ start: cursor, end: runEnd, delimiter: marker.repeat(length) });
    cursor = runEnd;
  }
  return delimiters;
}

function isCompletePair(text, openingStart, contentStart, contentEnd, closingEnd, delimiter) {
  const marker = delimiter[0];
  return contentStart < contentEnd
    && text[openingStart - 1] !== marker
    && text[contentStart] !== marker
    && text[contentEnd - 1] !== marker
    && text[closingEnd] !== marker;
}

function formattingEditsOverlap(edits) {
  const ordered = [...edits].sort((left, right) => left.start - right.start || left.end - right.end);
  return ordered.some((edit, index) => index > 0 && edit.start < ordered[index - 1].end);
}

function registerFormattingCommands(context, vscode) {
  for (const style of Object.keys(FORMATTERS)) {
    const command = `talewright.toggle${style === 'bold' ? 'Bold' : 'Italic'}`;
    context.subscriptions.push(vscode.commands.registerCommand(command, async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor || editor.document.languageId !== 'tale') return;

      const text = editor.document.getText();
      const plans = editor.selections.flatMap((selection, index) => {
        const start = editor.document.offsetAt(selection.start);
        const end = editor.document.offsetAt(selection.end);
        const plan = planFormattingEdit(text, start, end, style);
        return plan ? [{ ...plan, index, reversed: selection.isReversed }] : [];
      });
      if (plans.length === 0) return;
      if (formattingEditsOverlap(plans)) {
        await vscode.window.showWarningMessage('Selected ranges overlap; no formatting changes were made.');
        return;
      }

      const ordered = [...plans].sort((left, right) => left.start - right.start);
      let precedingDelta = 0;
      for (const plan of ordered) {
        plan.finalSelectionStart = plan.start + precedingDelta + plan.selectionOffset;
        plan.finalSelectionEnd = plan.finalSelectionStart + plan.selectedText.length;
        precedingDelta += plan.replacement.length - (plan.end - plan.start);
      }

      const applied = await editor.edit(editBuilder => {
        for (const plan of ordered) {
          editBuilder.replace(
            new vscode.Range(editor.document.positionAt(plan.start), editor.document.positionAt(plan.end)),
            plan.replacement,
          );
        }
      });
      if (!applied) {
        await vscode.window.showErrorMessage('Could not apply Talemark formatting to the selected text.');
        return;
      }

      editor.selections = plans
        .sort((left, right) => left.index - right.index)
        .map(plan => {
          const anchor = editor.document.positionAt(
            plan.reversed ? plan.finalSelectionEnd : plan.finalSelectionStart);
          const active = editor.document.positionAt(
            plan.reversed ? plan.finalSelectionStart : plan.finalSelectionEnd);
          return new vscode.Selection(anchor, active);
        });
    }));
  }
}

module.exports = { formattingEditsOverlap, planFormattingEdit, registerFormattingCommands };
