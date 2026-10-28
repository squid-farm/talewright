namespace Talewright.Player.E2E.Tests;

public sealed class RequestFailureTests
{
    [Theory]
    [InlineData(true, "image", "blob:http://localhost/image", "Load request cancelled", true)]
    [InlineData(false, "image", "blob:http://localhost/image", "Load request cancelled", false)]
    [InlineData(true, "image", "http://localhost/image.png", "Load request cancelled", false)]
    [InlineData(true, "script", "blob:http://localhost/script", "Load request cancelled", false)]
    [InlineData(true, "image", "blob:http://localhost/image", "Not allowed to load local resource", false)]
    [InlineData(true, "image", "blob:http://localhost/image", null, false)]
    public void Only_explicit_reload_blob_image_cancellations_are_expected(
        bool reloading, string resourceType, string url, string? failure, bool expected)
    {
        Assert.Equal(expected, PlayerTest.IsReloadCancellation(reloading, resourceType, url, failure));
    }
}
