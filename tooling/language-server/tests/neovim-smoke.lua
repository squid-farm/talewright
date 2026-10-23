local root = vim.fn.tempname()
vim.fn.mkdir(root, "p")
vim.fn.writefile({
  "id: nvim-smoke", "title: Neovim smoke", "start: start",
  "characters:", "  player: {first-name: Ada}",
}, root .. "/story.talewright.yaml")
vim.fn.writefile({ "=== start", "* [Go] -> missing" }, root .. "/start.tale")
local ok, failure = pcall(function()
  vim.cmd.edit(vim.fn.fnameescape(root .. "/start.tale"))
  local buffer = vim.api.nvim_get_current_buf()
  local id = vim.lsp.start({
    name = "talewright",
    cmd = { "dotnet", assert(vim.env.TALEWRIGHT_LSP_DLL), "--stdio" },
    root_dir = root,
  })
  assert(id, "Language server did not start")
  assert(vim.wait(15000, function()
    return #vim.diagnostic.get(buffer) > 0
  end, 50), "Compiler diagnostics never arrived")
  local diagnostic = vim.diagnostic.get(buffer)[1]
  assert(diagnostic.source == "talewright", "Expected shared compiler source")
  assert(diagnostic.code:match("^TW"), "Expected compiler diagnostic code")
  local client = assert(vim.lsp.get_client_by_id(id))
  assert(client.server_capabilities.completionProvider, "Server did not advertise completion")
  local response = client:request_sync("textDocument/completion", {
    textDocument = { uri = vim.uri_from_bufnr(buffer) },
    position = { line = 1, character = 17 },
  }, 10000, buffer)
  assert(response and response.result, "Shared completion request failed")
  local found = false
  for _, item in ipairs(response.result.items) do
    if item.label == "missing" then found = true end
  end
  assert(not found, "Unresolved divert is not a scene declaration")
  vim.api.nvim_buf_set_lines(buffer, 0, -1, false, { "=== start", "Hello.", "-> END" })
  assert(vim.wait(15000, function()
    return #vim.diagnostic.get(buffer) == 0
  end, 50), "Unsaved fix did not clear diagnostics")
  assert(vim.fn.readfile(root .. "/start.tale")[2]:match("missing"), "Test accidentally saved the buffer")
  vim.api.nvim_buf_set_lines(buffer, 0, -1, false, { "=== start", "-> sta" })
  response = client:request_sync("textDocument/completion", {
    textDocument = { uri = vim.uri_from_bufnr(buffer) },
    position = { line = 1, character = 6 },
  }, 10000, buffer)
  assert(response and response.result, "Unsaved completion request failed")
  found = false
  for _, item in ipairs(response.result.items) do
    if item.label == "start" then
      assert(item.textEdit.range.start.character == 3 and item.textEdit.range["end"].character == 6,
        "Completion did not replace the partial divert token")
      found = true
    end
  end
  assert(found, "Unsaved buffer did not offer the start scene")
  assert(client.server_capabilities.hoverProvider, "Server did not advertise hover")
  vim.api.nvim_buf_set_lines(buffer, 0, -1, false, { "=== start", "-> start" })
  response = client:request_sync("textDocument/hover", {
    textDocument = { uri = vim.uri_from_bufnr(buffer) },
    position = { line = 1, character = 5 },
  }, 10000, buffer)
  assert(response and response.result, "Shared hover request failed")
  assert(response.result.contents.value:match("Scene") and response.result.contents.value:match("Declared at"),
    "Hover omitted symbol kind or declaration context")
  assert(response.result.range.start.character == 3 and response.result.range["end"].character == 8,
    "Hover did not return the exact source range")
  vim.lsp.stop_client(id)
  assert(vim.wait(5000, function() return vim.lsp.get_client_by_id(id) == nil end, 50), "Server did not shut down")
  print("Neovim LSP smoke passed: diagnostics, unsaved fixes, completion, source-aware hover, shutdown.")
end)
vim.fn.delete(root .. "/start.tale")
vim.fn.delete(root .. "/story.talewright.yaml")
vim.fn.delete(root, "d")
if not ok then error(failure) end
vim.cmd("qa!")
