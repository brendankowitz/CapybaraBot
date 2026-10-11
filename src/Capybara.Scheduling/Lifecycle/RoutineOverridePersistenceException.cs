namespace Capybara.Scheduling.Lifecycle;

/// <summary>
/// The user's routine decision could not be written to the scheduling database, so a restart would not keep it
/// (FR-RTN-04). <see cref="RoutineRegistry"/> fails safe: a disable is in force anyway, an enable is not applied.
/// </summary>
public sealed class RoutineOverridePersistenceException(string message, Exception innerException) : IOException(message, innerException);
