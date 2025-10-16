using System.Collections.Generic;
using System.Threading;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Provider;

internal interface IProviderClient {
    IAsyncEnumerable<ModelOutputDelta> CallModelAsync(LlmRequest request, CancellationToken cancellationToken);
}
