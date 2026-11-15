namespace Daqifi.Desktop.Device;

/// <summary>
/// Thrown when a start is refused because the device did not answer the capability read within
/// <see cref="AbstractStreamingDevice.CapabilityRefreshTimeout"/> — almost always because another
/// operation, such as an SD card download, owns the device (#547).
/// </summary>
/// <remarks>
/// <para>
/// The start is refused rather than sent against the last cap the device stated. That cap was
/// read at connect, when no channel was enabled, so it is usually zero, and Core reads zero as
/// "no cap". A start queued behind the download at the chosen rate is refused by the firmware
/// (SCPI <c>-222</c>) when the channel set cannot sustain it, and the session shows LOGGING on
/// with no samples. Refusing here keeps the toggle off and tells the user why.
/// </para>
/// <para>
/// It needs a TYPE so that a caller can tell this ordinary device condition from a defect:
/// <see cref="AbstractStreamingDevice.StartSdCardLogging"/> logs any other failure as an Error,
/// which raises a Sentry event. Derives from <see cref="InvalidOperationException"/>, as
/// <see cref="SdOperationBlockedException"/> does, so existing handlers of that type still see it.
/// </para>
/// </remarks>
public sealed class DeviceBusyException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceBusyException"/> class.
    /// </summary>
    /// <param name="waited">How long the read waited before it was cancelled.</param>
    /// <param name="innerException">The cancellation that ended the read.</param>
    public DeviceBusyException(TimeSpan waited, Exception? innerException = null)
        : base(
            $"The device did not answer within {waited.TotalSeconds:0.#} s. Another operation, such as " +
            "an SD card import, is using it. Try again when that operation ends.",
            innerException)
    {
    }
}
