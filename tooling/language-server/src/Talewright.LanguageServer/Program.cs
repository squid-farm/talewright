using Talewright.LanguageServer;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("talewright-lsp --stdio\nLong-running LSP server. Requires .NET 10. Protocol on stdout; errors on stderr.");
    return 0;
}
if (args is not ["--stdio"])
{
    Console.Error.WriteLine("Expected --stdio. Use --help for usage.");
    return 2;
}
try
{
    return await new LanguageServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error).RunAsync();
}
catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"TWLSP001: {exception.Message}");
    return 1;
}
