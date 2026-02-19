using System.Buffers;

namespace Atelia.StateJournal3;

public class DurableList<T> : DurableObject
where T : DurableBase {
    public override bool HasChanges {
        get {
            throw new NotImplementedException();
        }
    }

    public override void DiscardChanges() {
        throw new NotImplementedException();
    }

    public override void OnCommitSucceeded() {
        throw new NotImplementedException();
    }

    public override void WritePendingDiff(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}
