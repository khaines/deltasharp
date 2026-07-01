namespace DeltaSharp;

/// <summary>
/// The deterministic public error thrown when a member is called on a <see cref="SparkSession"/>
/// that has already been stopped (<see cref="SparkSession.Stop"/>) or disposed
/// (<see cref="SparkSession.Dispose"/>).
/// </summary>
/// <remarks>
/// Mirrors Apache Spark's behavior of raising an <c>IllegalStateException</c>
/// (<i>"Cannot call methods on a stopped SparkSession"</i>) for the same situation;
/// <see cref="InvalidOperationException"/> is the .NET analogue. The message is built only from
/// deterministic inputs (the member name and the configured application name) so it is stable and
/// catchable. See <c>docs/engineering/design/sparksession-lifecycle.md</c>.
/// </remarks>
public sealed class SessionStoppedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="SessionStoppedException"/> class.</summary>
    public SessionStoppedException()
        : base("Cannot call methods on a stopped or disposed SparkSession.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionStoppedException"/> class with a
    /// deterministic message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public SessionStoppedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionStoppedException"/> class with a
    /// deterministic message and an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this error.</param>
    public SessionStoppedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates a <see cref="SessionStoppedException"/> whose message names the member that was called
    /// and the session's application name, with remediation guidance.
    /// </summary>
    /// <param name="memberName">The session member that was invoked on the stopped session.</param>
    /// <param name="appName">The session's configured <c>spark.app.name</c>, if any.</param>
    /// <returns>A new <see cref="SessionStoppedException"/> with a deterministic message.</returns>
    internal static SessionStoppedException ForMember(string memberName, string? appName)
    {
        string app = string.IsNullOrEmpty(appName) ? "<unnamed>" : appName;
        return new SessionStoppedException(
            $"Cannot call '{memberName}' on SparkSession 'app={app}': the session was stopped or " +
            "disposed. Create a new session with SparkSession.Builder().GetOrCreate().");
    }
}
