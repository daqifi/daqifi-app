using System.IO;

namespace Daqifi.Desktop.Loggers;

/// <summary>
/// A log file the parser refused, in the terms an import surface reports it.
/// </summary>
/// <param name="Explanation">
/// Why the file was refused, as the parser itself put it: Core's parse exceptions name the
/// offending row and say what is wrong with it, in a sentence written for a user rather than for
/// a log. This is the half of the report the app cannot write for itself — "the file is not valid"
/// is the one thing the user cannot check by eye.
/// </param>
/// <param name="Guidance">
/// What to do about it. Deliberately free of the row text and the file name, like every other
/// user-facing guidance sentence in the import surfaces, so a batch import that skips several
/// files for the same reason reads it out once (see
/// <c>DeviceLogsViewModel.BuildImportAllSummary</c>).
/// </param>
public sealed record SdCardLogParseFailure(string Explanation, string Guidance)
{
    /// <summary>
    /// Builds the body of the "Import Failed" dialog: the parser's own explanation, then what to
    /// do about it. Named after <see cref="ImportTimestampQuality.BuildUserWarning"/>, and here
    /// for the same reason — so the wording can be pinned by a test without a dialog.
    /// </summary>
    public string BuildUserMessage() => $"{Explanation}{Environment.NewLine}{Environment.NewLine}{Guidance}";
}

/// <summary>
/// Decides whether an exception from an SD card log import is the parser refusing the file —
/// an expected condition for input the user chose — or something else.
///
/// <para>The counterpart of <c>SdCardFailureClassifier</c> for the file rather than the device,
/// and it draws the same distinction: a refused file is a property of the user's input, so it is
/// logged at Warning and never raises a Sentry issue, while anything else keeps the Error path.
/// A user picking a hand-edited, truncated or non-firmware log is not a fault in this app.</para>
///
/// <para>It lives here next to the importer, rather than beside the device-side classifier in
/// the view models, because the importer needs it too: the parse is lazy, so a row Core refuses
/// surfaces from inside <c>SdCardSessionImporter.WriteSamplesAsync</c>, which logged its own
/// Error for it before anything reached a view model.</para>
/// </summary>
public static class SdCardLogParseFailureClassifier
{
    #region Constants
    /// <summary>
    /// What to do about a file the parser refused. The two causes it has to cover are a file that
    /// never came off a DAQiFi device and one that was edited after it did — a spreadsheet round
    /// trip rewrites value formatting, which is exactly what Core's unit rule refuses — so the
    /// advice is to import the file as the device wrote it, and to fetch it again if that copy is
    /// gone.
    /// </summary>
    internal const string MALFORMED_LOG_GUIDANCE =
        "This file is not a log the device wrote, or it was changed after it was downloaded. " +
        "Import the file as the device wrote it, or download it from the device's SD card again.";

    /// <summary>
    /// Stands in for a refusal that carries no message of its own. Nothing in Core throws such an
    /// exception today — the parse messages are the point of the type — but a dialog that opens on
    /// a blank line would be worse than a plain sentence, so the explanation is never empty.
    /// </summary>
    internal const string UNSTATED_REFUSAL_EXPLANATION =
        "The log file could not be read: its contents are not in the format the parser expects.";
    #endregion

    #region Public Methods
    /// <summary>
    /// Classifies an exception raised while importing an SD card log file.
    /// </summary>
    /// <param name="ex">The exception to classify. Never null.</param>
    /// <returns>
    /// The refusal, or <c>null</c> when the failure is not the parser refusing the file — in which
    /// case the caller keeps whatever it does today, which for every import surface is an Error
    /// and the generic failure dialog.
    /// </returns>
    public static SdCardLogParseFailure? Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex switch
        {
            // Core's parse-level refusal, from the CSV/JSON value and unit rules: a row whose
            // values mix the volts form with whole numbers states no single unit, and reading it
            // either way would report a voltage wrong by a factor of 1000
            // (SdCardAnalogValueUnitRule, daqifi-core#868). Matched by TYPE and only this type:
            // an exception from the database write or the transport says nothing about the file
            // and must keep the Error path, and a suppressed Error costs more than a noisy one.
            //
            // Note the type is deliberately NOT chased through InnerException. Core throws this
            // from the parse itself, so an InvalidDataException wrapped in something else did not
            // come from here, and unwrapping would classify a defect that merely happened to
            // carry one.
            InvalidDataException => new SdCardLogParseFailure(
                Explanation: string.IsNullOrWhiteSpace(ex.Message)
                    ? UNSTATED_REFUSAL_EXPLANATION
                    : ex.Message,
                Guidance: MALFORMED_LOG_GUIDANCE),

            _ => null
        };
    }
    #endregion
}
