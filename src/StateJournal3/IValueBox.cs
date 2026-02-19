namespace Atelia.StateJournal3;

public interface IValueBox<out T> where T : notnull {
    T Value { get; }
}
