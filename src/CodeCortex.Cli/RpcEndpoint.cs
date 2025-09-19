using System.CommandLine;
using System.CommandLine.Binding;

namespace CodeCortex.Cli {
    public sealed record RpcEndpoint(string Host, int Port);

    public sealed class RpcEndpointBinder : BinderBase<RpcEndpoint> {
        private readonly Option<string> _hostOption;
        private readonly Option<int> _portOption;

        public RpcEndpointBinder(Option<string> hostOption, Option<int> portOption) {
            _hostOption = hostOption;
            _portOption = portOption;
        }

        protected override RpcEndpoint GetBoundValue(BindingContext bindingContext) {
            var host = bindingContext.ParseResult.GetValueForOption(_hostOption) ?? "127.0.0.1";
            var port = bindingContext.ParseResult.GetValueForOption(_portOption);
            return new RpcEndpoint(host, port);
        }
    }
}

