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
