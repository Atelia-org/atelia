namespace Atelia.StateJournal;

public enum GetStatus : byte {
    Success = 0,
    PrecisionLost,
    OverflowedToInfinity,
    Saturated,
    TypeMismatch,
    NotFound,
}
