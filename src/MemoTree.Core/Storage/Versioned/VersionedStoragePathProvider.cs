namespace MemoTree.Core.Storage.Versioned {
    /// <summary>
    /// 版本化存储文件路径提供器
    /// </summary>
    public class VersionedStoragePathProvider<TKey>
    where TKey : notnull {
        private readonly VersionedStorageOptions _options;
        private readonly IKeySerializer<TKey> _keySerializer;
        private readonly IVersionFormatter _versionFormatter;

        public VersionedStoragePathProvider(
            VersionedStorageOptions options,
            IKeySerializer<TKey> keySerializer,
            IVersionFormatter versionFormatter
        ) {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _keySerializer = keySerializer ?? throw new ArgumentNullException(nameof(keySerializer));
            _versionFormatter = versionFormatter ?? throw new ArgumentNullException(nameof(versionFormatter));

            _options.Validate();
        }

        /// <summary>
        /// 获取存储根目录
        /// </summary>
        public string GetStorageRoot() => _options.StorageRoot;

        /// <summary>
        /// 获取数据目录路径
        /// </summary>
        public string GetDataDirectory()
        => Path.Combine(_options.StorageRoot, _options.DataDirectory);

        /// <summary>
        /// 获取版本指针文件路径
        /// </summary>
        public string GetVersionFilePath()
        => Path.Combine(_options.StorageRoot, _options.VersionFile);

        /// <summary>
        /// 获取事务日志目录路径
        /// </summary>
        public string GetJournalsDirectory()
        => Path.Combine(_options.StorageRoot, _options.JournalsDirectory);

        /// <summary>
        /// 获取数据文件路径
        /// </summary>
        public string GetDataFilePath(TKey key, long version) {
            var keyString = _keySerializer.Serialize(key);
            var versionString = _versionFormatter.FormatVersion(version);
            var fileName = $"{keyString}.{versionString}{_options.FileExtension}";
            return Path.Combine(GetDataDirectory(), fileName);
        }

        /// <summary>
        /// 获取操作日志文件路径
        /// </summary>
        public string GetJournalFilePath(string operationId) {
            var fileName = $"{operationId}.log.json";
            return Path.Combine(GetJournalsDirectory(), fileName);
        }

        /// <summary>
        /// 确保目录结构存在
        /// </summary>
        public void EnsureDirectoriesExist() {
            Directory.CreateDirectory(GetDataDirectory());
            Directory.CreateDirectory(GetJournalsDirectory());

            var storageRoot = GetStorageRoot();
            if (!Directory.Exists(storageRoot)) {
                Directory.CreateDirectory(storageRoot);
            }
        }

        /// <summary>
        /// 尝试从文件名解析键和版本
        /// 文件名格式：{serialized-key}.{version}
        /// </summary>
        public (TKey key, long version)? TryParseFileName(string fileName) {
            if (string.IsNullOrWhiteSpace(fileName)) { return null; }
            var parts = fileName.Split('.');
            if (parts.Length != 2) { return null; }
            var keyPart = parts[0];
            var versionPart = parts[1];

            // 验证key部分
            if (string.IsNullOrWhiteSpace(keyPart)) { return null; }
            var version = _versionFormatter.ParseVersion(versionPart);
            if (!version.HasValue) { return null; }
            try {
                var key = _keySerializer.Deserialize(keyPart);
                return (key, version.Value);
            }
            catch {
                // key反序列化失败
                return null;
            }
        }

        /// <summary>
        /// 扫描数据目录获取所有文件
        /// </summary>
        public IEnumerable<FileInfo<TKey>> ScanDataFiles() {
            var dataDir = GetDataDirectory();
            if (!Directory.Exists(dataDir)) {
                yield break;
            }

            var pattern = $"*{_options.FileExtension}";
            var files = Directory.GetFiles(dataDir, pattern);

            foreach (var file in files) {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var parseResult = TryParseFileName(fileName);

                if (parseResult.HasValue) {
                    var fileInfo = new System.IO.FileInfo(file);
                    yield return new FileInfo<TKey> {
                        Key = parseResult.Value.key,
                        Version = parseResult.Value.version,
                        FilePath = file,
                        LastModified = fileInfo.LastWriteTime,
                        FileSize = fileInfo.Length
                    };
                }
            }
        }

        /// <summary>
        /// 查找指定键的最新版本文件
        /// </summary>
        public FileInfo<TKey>? FindLatestVersionFile(TKey key, long maxVersion = long.MaxValue) {
            return ScanDataFiles()
            .Where(f => f.Key.Equals(key) && f.Version <= maxVersion)
            .OrderByDescending(f => f.Version)
            .FirstOrDefault();
        }

        /// <summary>
        /// 扫描事务日志文件
        /// </summary>
        public IEnumerable<string> ScanJournalFiles() {
            var journalsDir = GetJournalsDirectory();
            if (!Directory.Exists(journalsDir)) {
                yield break;
            }

            var files = Directory.GetFiles(journalsDir, "*.log.json");
            foreach (var file in files) {
                yield return file;
            }
        }
    }
}
