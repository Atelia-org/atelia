using Microsoft.Data.Sqlite;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

internal enum HistoryTimelineTrieKind : byte {
    Row = 1,
    End = 2
}

internal sealed class SqliteSelectedPathTrie {
    private const int MaximumNodeBytes = 8_228;
    private const string NodeHashDomain =
        "atelia.history-timeline.selected-path-index-node.v1";

    internal string InsertRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? rootDigest,
        HistoryRowId rowId,
        ref int insertedNodeCount
    ) => Insert(
        connection,
        transaction,
        HistoryTimelineTrieKind.Row,
        rootDigest,
        Convert.FromHexString(rowId.Value),
        rowId,
        ref insertedNodeCount
    );

    internal string InsertEnd(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? rootDigest,
        EventAddress endInclusive,
        HistoryRowId rowId,
        ref int insertedNodeCount
    ) {
        byte[] key = new byte[EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(endInclusive, key);
        return Insert(
            connection,
            transaction,
            HistoryTimelineTrieKind.End,
            rootDigest,
            key,
            rowId,
            ref insertedNodeCount
        );
    }

    internal string ComputeRowExtension(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string? rootDigest,
        HistoryRowId rowId
    ) => ComputeExtension(
        connection,
        transaction,
        HistoryTimelineTrieKind.Row,
        rootDigest,
        Convert.FromHexString(rowId.Value),
        rowId
    );

    internal string ComputeEndExtension(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string? rootDigest,
        EventAddress endInclusive,
        HistoryRowId rowId
    ) {
        byte[] key = new byte[EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(endInclusive, key);
        return ComputeExtension(
            connection,
            transaction,
            HistoryTimelineTrieKind.End,
            rootDigest,
            key,
            rowId
        );
    }

    internal HistoryRowId? LookupRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string? rootDigest,
        HistoryRowId rowId
    ) => Lookup(
        connection,
        transaction,
        HistoryTimelineTrieKind.Row,
        rootDigest,
        Convert.FromHexString(rowId.Value)
    );

    internal HistoryRowId? LookupEnd(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string? rootDigest,
        EventAddress endInclusive
    ) {
        byte[] key = new byte[EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(endInclusive, key);
        return Lookup(
            connection,
            transaction,
            HistoryTimelineTrieKind.End,
            rootDigest,
            key
        );
    }

    internal EndBoundaryProbe OpenEndBoundaryProbe(
        SqliteConnection connection,
        string? rootDigest,
        Action? beforeLookupQuery = null
    ) => new(connection, rootDigest, beforeLookupQuery);

    internal sealed class EndBoundaryProbe : IDisposable {
        private readonly string? _rootDigest;
        private readonly SqliteCommand _readNode;
        private readonly SqliteParameter _digest;
        private readonly Action? _beforeLookupQuery;

        internal EndBoundaryProbe(
            SqliteConnection connection,
            string? rootDigest,
            Action? beforeLookupQuery
        ) {
            _rootDigest = rootDigest;
            _beforeLookupQuery = beforeLookupQuery;
            _readNode = connection.CreateCommand();
            _readNode.CommandText = """
                SELECT length(canonical), canonical
                FROM selected_path_nodes
                WHERE node_digest = $digest;
                """;
            _digest = _readNode.Parameters.Add(
                "$digest",
                SqliteType.Text
            );
            _readNode.Prepare();
        }

        internal HistoryRowId? Lookup(EventAddress endInclusive) {
            byte[] key = new byte[EventAddressCodec.EventAddressLength];
            EventAddressCodec.Encode(endInclusive, key);
            string? nodeDigest = _rootDigest;
            for (int depth = 0; depth < key.Length; depth++) {
                if (nodeDigest is null) {
                    return null;
                }
                TrieBranch branch = DecodeBranch(ReadNode(nodeDigest));
                if (branch.Kind != HistoryTimelineTrieKind.End
                    || branch.Depth != depth) {
                    throw new InvalidDataException(
                        "Selected-path trie branch scope mismatch."
                    );
                }
                if (!branch.Children.TryGetValue(
                        key[depth],
                        out nodeDigest)) {
                    return null;
                }
            }
            if (nodeDigest is null) {
                return null;
            }
            TrieLeaf leaf = DecodeLeaf(ReadNode(nodeDigest));
            if (leaf.Kind != HistoryTimelineTrieKind.End
                || !leaf.Key.AsSpan().SequenceEqual(key)) {
                throw new InvalidDataException(
                    "Selected-path trie leaf scope mismatch."
                );
            }
            return leaf.RowId;
        }

        private byte[] ReadNode(string digest) {
            _beforeLookupQuery?.Invoke();
            _digest.Value = digest;
            using SqliteDataReader reader = _readNode.ExecuteReader();
            if (!reader.Read()) {
                throw new InvalidDataException(
                    "Selected-path trie references a missing node."
                );
            }
            long length = reader.GetInt64(0);
            if (length is < 1 or > MaximumNodeBytes) {
                throw new InvalidDataException(
                    "Selected-path trie node exceeds its byte bound."
                );
            }
            byte[] bytes = reader.GetFieldValue<byte[]>(1);
            if (bytes.Length != length
                || !string.Equals(
                    ComputeNodeDigest(bytes),
                    digest,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "Selected-path trie node digest mismatch."
                );
            }
            return bytes;
        }

        public void Dispose() => _readNode.Dispose();
    }

    internal static IReadOnlyList<string> VerifyCanonicalNode(
        string expectedDigest,
        byte[] canonical
    ) {
        if (canonical.Length is < 1 or > MaximumNodeBytes
            || !string.Equals(
                HistoryTimelineHash.Compute(NodeHashDomain, canonical),
                expectedDigest,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Selected-path trie node digest mismatch."
            );
        }
        if (canonical.Length < 2) {
            throw new InvalidDataException(
                "Selected-path trie node is truncated."
            );
        }
        if (canonical[1] == 0) {
            TrieBranch branch = DecodeBranch(canonical);
            int maximumDepth = branch.Kind
                == HistoryTimelineTrieKind.Row
                    ? 31
                    : EventAddressCodec.EventAddressLength - 1;
            if (branch.Depth > maximumDepth) {
                throw new InvalidDataException(
                    "Selected-path trie branch depth is invalid."
                );
            }
            return branch.Children.Values.ToArray();
        }
        if (canonical[1] == 1) {
            TrieLeaf leaf = DecodeLeaf(canonical);
            int expectedKeyBytes = leaf.Kind
                == HistoryTimelineTrieKind.Row
                    ? 32
                    : EventAddressCodec.EventAddressLength;
            if (leaf.Key.Length != expectedKeyBytes) {
                throw new InvalidDataException(
                    "Selected-path trie leaf key length is invalid."
                );
            }
            return [];
        }
        throw new InvalidDataException(
            "Selected-path trie node kind is invalid."
        );
    }

    private string Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HistoryTimelineTrieKind kind,
        string? rootDigest,
        byte[] key,
        HistoryRowId rowId,
        ref int insertedNodeCount
    ) => InsertAt(
        connection,
        transaction,
        kind,
        rootDigest,
        key,
        rowId,
        depth: 0,
        persist: true,
        ref insertedNodeCount
    );

    private string ComputeExtension(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryTimelineTrieKind kind,
        string? rootDigest,
        byte[] key,
        HistoryRowId rowId
    ) {
        int ignored = 0;
        return InsertAt(
            connection,
            transaction,
            kind,
            rootDigest,
            key,
            rowId,
            depth: 0,
            persist: false,
            ref ignored
        );
    }

    private string InsertAt(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryTimelineTrieKind kind,
        string? nodeDigest,
        byte[] key,
        HistoryRowId rowId,
        int depth,
        bool persist,
        ref int insertedNodeCount
    ) {
        if (depth == key.Length) {
            byte[] leaf = EncodeLeaf(kind, key, rowId);
            if (nodeDigest is not null) {
                byte[] existing = ReadNode(
                    connection,
                    transaction,
                    nodeDigest
                );
                if (!existing.AsSpan().SequenceEqual(leaf)) {
                    throw new InvalidDataException(
                        "Selected-path trie leaf collision."
                    );
                }
                return nodeDigest;
            }
            return persist
                ? StoreNode(
                    connection,
                    transaction!,
                    leaf,
                    ref insertedNodeCount
                )
                : ComputeNodeDigest(leaf);
        }

        SortedDictionary<byte, string> children;
        if (nodeDigest is null) {
            children = [];
        }
        else {
            TrieBranch branch = DecodeBranch(ReadNode(
                connection,
                transaction,
                nodeDigest
            ));
            if (branch.Kind != kind || branch.Depth != depth) {
                throw new InvalidDataException(
                    "Selected-path trie branch scope mismatch."
                );
            }
            children = [];
            foreach ((byte existingSlot, string digest)
                     in branch.Children) {
                children.Add(existingSlot, digest);
            }
        }

        byte slot = key[depth];
        children.TryGetValue(slot, out string? childDigest);
        string nextChild = InsertAt(
            connection,
            transaction,
            kind,
            childDigest,
            key,
            rowId,
            depth + 1,
            persist,
            ref insertedNodeCount
        );
        if (string.Equals(
                childDigest,
                nextChild,
                StringComparison.Ordinal)) {
            return nodeDigest!;
        }
        children[slot] = nextChild;
        byte[] encodedBranch = EncodeBranch(kind, depth, children);
        return persist
            ? StoreNode(
                connection,
                transaction!,
                encodedBranch,
                ref insertedNodeCount
            )
            : ComputeNodeDigest(encodedBranch);
    }

    private HistoryRowId? Lookup(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryTimelineTrieKind kind,
        string? rootDigest,
        byte[] key
    ) {
        string? nodeDigest = rootDigest;
        for (int depth = 0; depth < key.Length; depth++) {
            if (nodeDigest is null) {
                return null;
            }
            TrieBranch branch = DecodeBranch(ReadNode(
                connection,
                transaction,
                nodeDigest
            ));
            if (branch.Kind != kind || branch.Depth != depth) {
                throw new InvalidDataException(
                    "Selected-path trie branch scope mismatch."
                );
            }
            if (!branch.Children.TryGetValue(
                    key[depth],
                    out nodeDigest)) {
                return null;
            }
        }
        if (nodeDigest is null) {
            return null;
        }
        TrieLeaf leaf = DecodeLeaf(ReadNode(
            connection,
            transaction,
            nodeDigest
        ));
        if (leaf.Kind != kind
            || !leaf.Key.AsSpan().SequenceEqual(key)) {
            throw new InvalidDataException(
                "Selected-path trie leaf scope mismatch."
            );
        }
        return leaf.RowId;
    }

    private static string StoreNode(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] canonical,
        ref int insertedNodeCount
    ) {
        if (canonical.Length > MaximumNodeBytes) {
            throw new InvalidDataException(
                "Selected-path trie node exceeds its byte bound."
            );
        }
        string digest = ComputeNodeDigest(canonical);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO selected_path_nodes(
                node_digest, canonical
            ) VALUES ($digest, $canonical);
            """;
        insert.Parameters.AddWithValue("$digest", digest);
        insert.Parameters.AddWithValue("$canonical", canonical);
        int changed = insert.ExecuteNonQuery();
        byte[] stored = ReadNode(connection, transaction, digest);
        if (!stored.AsSpan().SequenceEqual(canonical)) {
            throw new InvalidDataException(
                "Selected-path trie node digest collision."
            );
        }
        insertedNodeCount = checked(insertedNodeCount + changed);
        return digest;
    }

    private static string ComputeNodeDigest(byte[] canonical) {
        if (canonical.Length > MaximumNodeBytes) {
            throw new InvalidDataException(
                "Selected-path trie node exceeds its byte bound."
            );
        }
        return HistoryTimelineHash.Compute(NodeHashDomain, canonical);
    }

    private static byte[] ReadNode(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string digest
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT length(canonical), canonical
            FROM selected_path_nodes
            WHERE node_digest = $digest;
            """;
        command.Parameters.AddWithValue("$digest", digest);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw new InvalidDataException(
                "Selected-path trie references a missing node."
            );
        }
        long length = reader.GetInt64(0);
        if (length is < 1 or > MaximumNodeBytes) {
            throw new InvalidDataException(
                "Selected-path trie node exceeds its byte bound."
            );
        }
        byte[] bytes = reader.GetFieldValue<byte[]>(1);
        if (bytes.Length != length) {
            throw new InvalidDataException(
                "Selected-path trie node length changed while reading."
            );
        }
        string actual = HistoryTimelineHash.Compute(
            NodeHashDomain,
            bytes
        );
        if (!string.Equals(
                actual,
                digest,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Selected-path trie node digest mismatch."
            );
        }
        return bytes;
    }

    private static byte[] EncodeBranch(
        HistoryTimelineTrieKind kind,
        int depth,
        IReadOnlyDictionary<byte, string> children
    ) {
        if (depth is < 0 or > byte.MaxValue
            || children.Count is < 1 or > 256) {
            throw new InvalidDataException(
                "Selected-path trie branch is out of bounds."
            );
        }
        byte[] bytes = new byte[
            4 + 32 + checked(children.Count * 32)
        ];
        bytes[0] = 1;
        bytes[1] = 0;
        bytes[2] = (byte)kind;
        bytes[3] = checked((byte)depth);
        int childOffset = 36;
        foreach ((byte slot, string digest) in children
                     .OrderBy(static child => child.Key)) {
            bytes[4 + (slot / 8)] |= checked((byte)(1 << (slot % 8)));
            byte[] digestBytes = Convert.FromHexString(digest);
            if (digestBytes.Length != 32) {
                throw new InvalidDataException(
                    "Selected-path trie child digest is invalid."
                );
            }
            digestBytes.CopyTo(bytes, childOffset);
            childOffset += digestBytes.Length;
        }
        return bytes;
    }

    private static TrieBranch DecodeBranch(byte[] bytes) {
        if (bytes.Length < 68
            || bytes[0] != 1
            || bytes[1] != 0
            || !Enum.IsDefined((HistoryTimelineTrieKind)bytes[2])) {
            throw new InvalidDataException(
                "Selected-path trie branch is invalid."
            );
        }
        int childCount = 0;
        for (int index = 4; index < 36; index++) {
            childCount += System.Numerics.BitOperations.PopCount(
                bytes[index]
            );
        }
        if (bytes.Length != 36 + checked(childCount * 32)) {
            throw new InvalidDataException(
                "Selected-path trie branch length is invalid."
            );
        }
        var children = new SortedDictionary<byte, string>();
        int childOffset = 36;
        for (int slot = 0; slot < 256; slot++) {
            if ((bytes[4 + (slot / 8)]
                    & (1 << (slot % 8))) == 0) {
                continue;
            }
            string digest = Convert.ToHexString(
                bytes.AsSpan(childOffset, 32)
            ).ToLowerInvariant();
            children.Add(checked((byte)slot), digest);
            childOffset += 32;
        }
        return new TrieBranch(
            (HistoryTimelineTrieKind)bytes[2],
            bytes[3],
            children
        );
    }

    private static byte[] EncodeLeaf(
        HistoryTimelineTrieKind kind,
        byte[] key,
        HistoryRowId rowId
    ) {
        byte[] rowBytes = Convert.FromHexString(rowId.Value);
        byte[] bytes = new byte[4 + key.Length + rowBytes.Length];
        bytes[0] = 1;
        bytes[1] = 1;
        bytes[2] = (byte)kind;
        bytes[3] = checked((byte)key.Length);
        key.CopyTo(bytes, 4);
        rowBytes.CopyTo(bytes, 4 + key.Length);
        return bytes;
    }

    private static TrieLeaf DecodeLeaf(byte[] bytes) {
        if (bytes.Length < 4 + 1 + 32
            || bytes[0] != 1
            || bytes[1] != 1
            || !Enum.IsDefined((HistoryTimelineTrieKind)bytes[2])) {
            throw new InvalidDataException(
                "Selected-path trie leaf is invalid."
            );
        }
        int keyLength = bytes[3];
        if (bytes.Length != 4 + keyLength + 32) {
            throw new InvalidDataException(
                "Selected-path trie leaf length is invalid."
            );
        }
        byte[] key = bytes.AsSpan(4, keyLength).ToArray();
        string rowId = Convert.ToHexString(
            bytes.AsSpan(4 + keyLength, 32)
        ).ToLowerInvariant();
        return new TrieLeaf(
            (HistoryTimelineTrieKind)bytes[2],
            key,
            new HistoryRowId(rowId)
        );
    }

    private sealed record TrieBranch(
        HistoryTimelineTrieKind Kind,
        int Depth,
        IReadOnlyDictionary<byte, string> Children
    );

    private sealed record TrieLeaf(
        HistoryTimelineTrieKind Kind,
        byte[] Key,
        HistoryRowId RowId
    );
}
