import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { test } from 'node:test';

const require = createRequire(import.meta.url);
const {
  formattingEditsOverlap,
  planFormattingEdit,
  registerFormattingCommands,
} = require('../formatting.cjs');

test('formatting wraps selected text with the recommended delimiters', () => {
  assert.deepEqual(planFormattingEdit('plain', 0, 5, 'bold'), {
    start: 0,
    end: 5,
    replacement: '**plain**',
    selectionOffset: 2,
    selectedText: 'plain',
  });
  assert.deepEqual(planFormattingEdit('plain', 0, 5, 'italic'), {
    start: 0,
    end: 5,
    replacement: '_plain_',
    selectionOffset: 1,
    selectedText: 'plain',
  });
});

test('formatting toggles either supported spelling when delimiters surround the selection', () => {
  for (const [text, start, end] of [
    ['**bold**', 2, 6],
    ['__bold__', 2, 6],
  ]) {
    assert.deepEqual(planFormattingEdit(text, start, end, 'bold'), {
      start: 0,
      end: text.length,
      replacement: 'bold',
      selectionOffset: 0,
      selectedText: 'bold',
    });
  }
  for (const [text, start, end] of [
    ['*italic*', 1, 7],
    ['_italic_', 1, 7],
  ]) {
    assert.equal(planFormattingEdit(text, start, end, 'italic').replacement, 'italic');
  }
});

test('formatting toggles delimiters included in the selection and preserves nesting', () => {
  for (const [text, style, expected] of [
    ['**bold**', 'bold', 'bold'],
    ['__bold__', 'bold', 'bold'],
    ['*italic*', 'italic', 'italic'],
    ['_italic_', 'italic', 'italic'],
  ]) {
    assert.equal(planFormattingEdit(text, 0, text.length, style).replacement, expected);
  }
  assert.equal(planFormattingEdit('**bold**', 2, 6, 'italic').replacement, '_bold_');
  assert.equal(planFormattingEdit('_italic_', 1, 7, 'bold').replacement, '**italic**');
});

test('formatting removes enclosing styles through nested delimiters', () => {
  for (const [text, style, expected, selectionOffset] of [
    ['**_plain_**', 'bold', '_plain_', 1],
    ['__*plain*__', 'bold', '*plain*', 1],
    ['_**plain**_', 'italic', '**plain**', 2],
    ['*__plain__*', 'italic', '__plain__', 2],
  ]) {
    const start = text.indexOf('plain');
    const plan = planFormattingEdit(text, start, start + 'plain'.length, style);

    assert.deepEqual(plan, {
      start: 0,
      end: text.length,
      replacement: expected,
      selectionOffset,
      selectedText: 'plain',
    });
  }

  const text = '_plain_';
  const plan = planFormattingEdit(text, 1, 6, 'bold');
  assert.equal(text.slice(0, plan.start) + plan.replacement + text.slice(plan.end), '_**plain**_');
});

test('formatting resolves selections that cross complete delimiter boundaries', () => {
  for (const [text, start, end, style, expected, selectedText, selectionOffset] of [
    ['**Bold**', 1, 6, 'bold', 'Bold', 'Bold', 0],
    ['**Bold**', 2, 7, 'bold', 'Bold', 'Bold', 0],
    ['__Bold__', 1, 6, 'bold', 'Bold', 'Bold', 0],
    ['__Bold__', 2, 7, 'bold', 'Bold', 'Bold', 0],
    ['*Italic*', 0, 7, 'italic', 'Italic', 'Italic', 0],
    ['_Italic_', 1, 8, 'italic', 'Italic', 'Italic', 0],
    ['**Bold**', 2, 7, 'italic', '**_Bold_**', 'Bold', 3],
    ['__Bold__', 1, 7, 'italic', '__*Bold*__', 'Bold', 3],
    ['_Italic_', 1, 8, 'bold', '_**Italic**_', 'Italic', 3],
  ]) {
    const plan = planFormattingEdit(text, start, end, style);

    assert.equal(text.slice(0, plan.start) + plan.replacement + text.slice(plan.end), expected);
    assert.equal(plan.selectedText, selectedText);
    assert.equal(plan.selectionOffset, selectionOffset);
  }
});

test('italic formatting does not match part of a complete bold delimiter run', () => {
  const text = '**bold**';
  const plan = planFormattingEdit(text, 2, 6, 'italic');
  assert.equal(plan.start, 2);
  assert.equal(plan.end, 6);
  assert.equal(plan.replacement, '_bold_');
});

test('empty selections are no-ops and invalid ranges are rejected', () => {
  assert.equal(planFormattingEdit('plain', 2, 2, 'bold'), null);
  assert.throws(() => planFormattingEdit('plain', -1, 2, 'bold'), RangeError);
  assert.throws(() => planFormattingEdit('plain', 0, 6, 'bold'), RangeError);
});

test('overlapping multi-selection edits are rejected but adjacent edits are allowed', () => {
  const text = '**bold** plain';
  const wrapped = planFormattingEdit(text, 2, 6, 'bold');
  const nested = planFormattingEdit(text, 2, 4, 'bold');
  const adjacent = planFormattingEdit(text, 9, 14, 'bold');

  assert.equal(formattingEditsOverlap([wrapped, nested]), true);
  assert.equal(formattingEditsOverlap([wrapped, adjacent]), false);
});

test('overlapping multi-selection edits leave the document unchanged and notify the author', async () => {
  const commands = new Map();
  const warnings = [];
  let editCalls = 0;
  const editor = {
    document: {
      languageId: 'tale',
      getText: () => '**bold**',
      offsetAt: position => position,
    },
    selections: [
      { start: 2, end: 6, isReversed: false },
      { start: 2, end: 4, isReversed: false },
    ],
    edit: async () => { editCalls++; return true; },
  };
  const vscode = {
    commands: {
      registerCommand: (id, handler) => {
        commands.set(id, handler);
        return { dispose() {} };
      },
    },
    window: {
      activeTextEditor: editor,
      showWarningMessage: async message => warnings.push(message),
    },
  };
  registerFormattingCommands({ subscriptions: [] }, vscode);

  await commands.get('talewright.toggleBold')();

  assert.equal(editCalls, 0);
  assert.deepEqual(warnings, ['Selected ranges overlap; no formatting changes were made.']);
});

test('nested formatting toggles preserve reversed selections across removal and restoration', async () => {
  const commands = new Map();
  const text = '**_plain_**';
  const start = text.indexOf('plain');
  let documentText = text;
  const selection = (anchor, active) => ({
    start: Math.min(anchor, active),
    end: Math.max(anchor, active),
    isReversed: anchor > active,
  });
  const editor = {
    document: {
      languageId: 'tale',
      getText: () => documentText,
      offsetAt: position => position,
      positionAt: offset => offset,
    },
    selections: [selection(start + 'plain'.length, start)],
    edit: async callback => {
      const edits = [];
      callback({ replace: (range, replacement) => edits.push({ ...range, replacement }) });
      for (const edit of edits.sort((left, right) => right.start - left.start)) {
        documentText = documentText.slice(0, edit.start) + edit.replacement + documentText.slice(edit.end);
      }
      return true;
    },
  };
  const vscode = {
    commands: {
      registerCommand: (id, handler) => {
        commands.set(id, handler);
        return { dispose() {} };
      },
    },
    Range: function (start, end) { return { start, end }; },
    Selection: function (anchor, active) { return selection(anchor, active); },
    window: { activeTextEditor: editor },
  };
  registerFormattingCommands({ subscriptions: [] }, vscode);

  await commands.get('talewright.toggleBold')();
  assert.equal(documentText, '_plain_');
  assert.deepEqual(editor.selections[0], selection('plain'.length + 1, 1));

  await commands.get('talewright.toggleBold')();
  assert.equal(documentText, '_**plain**_');
  assert.deepEqual(editor.selections[0], selection('plain'.length + 3, 3));
});

test('partial-delimiter formatting preserves reversed selections and remains stable when repeated', async () => {
  const commands = new Map();
  let documentText = '**Bold**';
  const selection = (anchor, active) => ({
    start: Math.min(anchor, active),
    end: Math.max(anchor, active),
    isReversed: anchor > active,
  });
  const editor = {
    document: {
      languageId: 'tale',
      getText: () => documentText,
      offsetAt: position => position,
      positionAt: offset => offset,
    },
    selections: [selection(7, 2)],
    edit: async callback => {
      const edits = [];
      callback({ replace: (range, replacement) => edits.push({ ...range, replacement }) });
      for (const edit of edits.sort((left, right) => right.start - left.start)) {
        documentText = documentText.slice(0, edit.start) + edit.replacement + documentText.slice(edit.end);
      }
      return true;
    },
  };
  const vscode = {
    commands: {
      registerCommand: (id, handler) => {
        commands.set(id, handler);
        return { dispose() {} };
      },
    },
    Range: function (start, end) { return { start, end }; },
    Selection: function (anchor, active) { return selection(anchor, active); },
    window: { activeTextEditor: editor },
  };
  registerFormattingCommands({ subscriptions: [] }, vscode);

  await commands.get('talewright.toggleBold')();
  assert.equal(documentText, 'Bold');
  assert.deepEqual(editor.selections[0], selection(4, 0));

  await commands.get('talewright.toggleBold')();
  assert.equal(documentText, '**Bold**');
  assert.deepEqual(editor.selections[0], selection(6, 2));

  await commands.get('talewright.toggleBold')();
  assert.equal(documentText, 'Bold');
  assert.deepEqual(editor.selections[0], selection(4, 0));
});
