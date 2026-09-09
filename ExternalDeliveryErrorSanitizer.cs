namespace Common.Messaging;

public static class ExternalDeliveryErrorSanitizer
{
    public static string GetErrorCode(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            TimeoutException => "DELIVERY_TIMEOUT",
            HttpRequestException => "DELIVERY_HTTP_ERROR",
            UnauthorizedAccessException => "DELIVERY_UNAUTHORIZED",
            InvalidOperationException => "DELIVERY_INVALID_OPERATION",
            OperationCanceledException => "DELIVERY_CANCELLED",
            _ => "DELIVERY_FAILED"
        };
    }
}
