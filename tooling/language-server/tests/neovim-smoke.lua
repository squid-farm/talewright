local root = vim.fn.tempname()
vim.fn.mkdir(root, "p")
vim.fn.writefile({
  "id: nvim-smoke", "title: Neovim smoke", "start: start",
  "characters:", "  player: {first-name: Ada}",
}, root .. "/story.yaml")
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
  vim.api.nvim_buf_set_lines(buffer, 0, -1, false, { "=== start", "Hello.", "-> END" })
  assert(vim.wait(15000, function()
    return #vim.diagnostic.get(buffer) == 0
  end, 50), "Unsaved fix did not clear diagnostics")
  assert(vim.fn.readfile(root .. "/start.tale")[2]:match("missing"), "Test accidentally saved the buffer")
  vim.lsp.stop_client(id)
  assert(vim.wait(5000, function() return vim.lsp.get_client_by_id(id) == nil end, 50), "Server did not shut down")
  print("Neovim LSP smoke passed: compiler diagnostics, unsaved fixes, clearing, shutdown.")
end)
vim.fn.delete(root .. "/start.tale")
vim.fn.delete(root .. "/story.yaml")
vim.fn.delete(root, "d")
if not ok then error(failure) end
vim.cmd("qa!")
