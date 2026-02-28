namespace Atelia.StateJournal;

public abstract class DurableListBase {

}

/// <summary>替代<see cref="DurableList{DurableValue}"/></summary>
public class DurableList : DurableListBase {
    internal DurableList() { }
}

public class DurableList<T> : DurableListBase where T : notnull {
    internal DurableList() { }
}
