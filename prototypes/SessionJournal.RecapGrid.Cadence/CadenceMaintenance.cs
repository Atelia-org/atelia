using Atelia.EventJournal;

namespace Atelia.SessionJournal.RecapGrid.Cadence;

public static class RecapGridCadenceMaintenance {
    public static RecapGridCadenceInspectResult Inspect(
        string repositoryPath,
        RefId refId
    ) {
        RecapGridCadenceOpenResult opened = RecapGridCadenceFactory.OpenForMaintenance(
            repositoryPath, refId);
        if (opened is not RecapGridCadenceOpenResult.Opened available) {
            return opened switch {
                RecapGridCadenceOpenResult.Absent
                    => new RecapGridCadenceInspectResult.Absent(),
                RecapGridCadenceOpenResult.Busy
                    => new RecapGridCadenceInspectResult.Busy(),
                RecapGridCadenceOpenResult.UnsupportedSchema schema
                    => new RecapGridCadenceInspectResult.UnsupportedSchema(
                        schema.Version),
                RecapGridCadenceOpenResult.PlatformUnsupported
                    => new RecapGridCadenceInspectResult.PlatformUnsupported(),
                RecapGridCadenceOpenResult.Invalid invalid
                    => new RecapGridCadenceInspectResult.Invalid(
                        invalid.Code, invalid.Detail),
                _ => new RecapGridCadenceInspectResult.Invalid(
                    "CadenceInspectOutcomeInvalid",
                    "Cadence factory returned an unknown outcome.")
            };
        }
        using (available.Handle) {
            return available.Handle.Reader.ReadSnapshot() switch {
                RecapGridCadenceReadResult.Available result
                    => new RecapGridCadenceInspectResult.Available(
                        result.Snapshot),
                RecapGridCadenceReadResult.Busy
                    => new RecapGridCadenceInspectResult.Busy(),
                RecapGridCadenceReadResult.UnsupportedSchema schema
                    => new RecapGridCadenceInspectResult.UnsupportedSchema(
                        schema.Version),
                RecapGridCadenceReadResult.Invalid invalid
                    => new RecapGridCadenceInspectResult.Invalid(
                        invalid.Code, invalid.Detail),
                _ => new RecapGridCadenceInspectResult.Invalid(
                    "CadenceInspectReadInvalid",
                    "Cadence state could not be read.")
            };
        }
    }
}
