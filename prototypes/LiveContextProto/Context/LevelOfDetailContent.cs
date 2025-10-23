using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Atelia.LiveContextProto.Context;

// 分析后还是认为LOD概念比Verbosity概念更适合本项目
internal enum LevelOfDetail {
    Basic,
    Detail
}

internal sealed class LevelOfDetailContent {
    private readonly string _basic;
    private readonly string _detail;

    public LevelOfDetailContent(string basic, string detail) {
        _basic = basic ?? throw new ArgumentNullException(nameof(basic));
        _detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }

    public string Basic => _basic;

    public string Detail => _detail;

    public string GetContent(LevelOfDetail detailLevel)
        => detailLevel switch {
            LevelOfDetail.Basic => _basic,
            LevelOfDetail.Detail => Detail,
            _ => _basic
        };

    public static LevelOfDetailContent Join(string? separator, params IEnumerable<LevelOfDetailContent> items) {
        string basic = string.Join(separator, items.Select(x => x.Basic));
        string detail = string.Join(separator, items.Select(x => x.Detail));
        return new LevelOfDetailContent(basic, detail);
    }

    public static LevelOfDetailContent Join(string? separator, LevelOfDetailContent item0, LevelOfDetailContent item1) {
        string basic = string.Join(separator, item0.Basic, item1.Basic);
        string detail = string.Join(separator, item0.Detail, item1.Detail);
        return new LevelOfDetailContent(basic, detail);
    }

    public static LevelOfDetailContent Join(string? separator, LevelOfDetailContent item0, LevelOfDetailContent item1, LevelOfDetailContent item2) {
        string basic = string.Join(separator, item0.Basic, item1.Basic, item2.Basic);
        string detail = string.Join(separator, item0.Detail, item1.Detail, item2.Detail);
        return new LevelOfDetailContent(basic, detail);
    }

    public static LevelOfDetailContent Join(string? separator, LevelOfDetailContent item0, LevelOfDetailContent item1, LevelOfDetailContent item2, LevelOfDetailContent item3) {
        string basic = string.Join(separator, item0.Basic, item1.Basic, item2.Basic, item3.Basic);
        string detail = string.Join(separator, item0.Detail, item1.Detail, item2.Detail, item3.Detail);
        return new LevelOfDetailContent(basic, detail);
    }

    public static LevelOfDetailContent Join(string? separator, LevelOfDetailContent item0, LevelOfDetailContent item1, LevelOfDetailContent item2, LevelOfDetailContent item3, LevelOfDetailContent item4) {
        string basic = string.Join(separator, item0.Basic, item1.Basic, item2.Basic, item3.Basic, item4.Basic);
        string detail = string.Join(separator, item0.Detail, item1.Detail, item2.Detail, item3.Detail, item4.Detail);
        return new LevelOfDetailContent(basic, detail);
    }
}
