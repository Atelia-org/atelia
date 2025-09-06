CodeCortex.ServiceV2

Scope (M1):
- On-demand outline pipeline over a request-level Roslyn Solution snapshot (overlay-ready)
- Cognition Graph (minimal): Node records in memory placeholder; API surface only
- Prefetcher OFF; no networking yet; focus on library-level correctness and tests

Planned layout:
- Graph/Abstractions: IGraphEngine, NodeRecord, NodeInput, NodeKinds
- Overlay: DocumentOverlayStore (session-ready), no persistence
- Services: OutlineService (GetOutlineAsync signature)

Build: part of Atelia.sln (net9.0)
