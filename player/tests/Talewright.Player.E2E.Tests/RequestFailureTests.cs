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

    [Theory]
    [InlineData(": ResizeObserver loop completed with undelivered notifications.", true)]
    [InlineData("ResizeObserver loop completed with undelivered notifications.", true)]
    [InlineData("Error: ResizeObserver loop completed with undelivered notifications.", false)]
    [InlineData("TypeError: ResizeObserver loop completed with undelivered notifications.", false)]
    [InlineData(": ResizeObserver loop limit exceeded", false)]
    [InlineData("TypeError: undefined is not an object", false)]
    public void Only_the_resize_observer_loop_notice_is_expected(string error, bool expected)
    {
        Assert.Equal(expected, PlayerTest.IsResizeObserverLoopNotice(error));
    }
}
