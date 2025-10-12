using System.Collections.Generic;
using System.Threading;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Provider;

internal interface IProviderClient {
    IAsyncEnumerable<ModelOutputDelta> CallModelAsync(ProviderRequest request, CancellationToken cancellationToken);
}
