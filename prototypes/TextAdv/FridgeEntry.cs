using System.CommandLine;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

/// <summary>
/// PipeMux 入口：冰箱状态持久化测试 —— 验证进程间通讯 + StateJournal 持久状态。
///
/// 注册方法：
/// <code>
/// pmux :register fridge /path/to/Atelia.TextAdv.dll Atelia.TextAdv.FridgeEntry.BuildFridge
/// </code>
///
/// 状态：进程内单例 <see cref="Repository"/> + <see cref="DurableDict{String}"/>（mixed dict），跨 pmux 调用保留。
/// 冰箱模型：<c>eggs</c>（int）表示鸡蛋数量，<c>capacity</c>（int）表示最大容量。
/// 数据持久化到 <c>/tmp/atelia-textadv-fridge/</c>；pmux 进程重启后状态不丢失。
/// </summary>
public static class FridgeEntry {
    private const string RepoDir = "/tmp/atelia-textadv-fridge";
    private const int DefaultCapacity = 12;

    private static Repository _repo = OpenOrCreateRepo();
    private static DurableDict<string> _fridge = OpenOrCreateFridge();

    private static Repository OpenOrCreateRepo() {
        if (Directory.Exists(RepoDir)) {
            var openResult = Repository.Open(RepoDir);
            return openResult.Value!;
        }
        var createResult = Repository.Create(RepoDir);
        return createResult.Value!;
    }

    private static DurableDict<string> OpenOrCreateFridge() {
        var revResult = _repo.GetOrCreateBranch("main");
        var rev = revResult.Value!;

        if (rev.GraphRoot is DurableDict<string> existing) { return existing; }

        // 首次：创建新冰箱
        var fridge = rev.CreateDict<string>();
        fridge.Upsert("eggs", 0);
        fridge.Upsert("capacity", DefaultCapacity);
        _ = _repo.Commit(fridge).Value;
        return fridge;
    }

    public static RootCommand BuildFridge() {
        var root = new RootCommand("冰箱状态测试 —— 验证 PipeMux 进程间通讯 + StateJournal 持久状态");

        root.Add(BuildPutEggCommand());
        root.Add(BuildGetEggCommand());
        root.Add(BuildStatusCommand());
        root.Add(BuildResetCommand());

        return root;
    }

    // ─────────────────────────────────────────
    // put-egg
    // ─────────────────────────────────────────

    private static Command BuildPutEggCommand() {
        var countOpt = new Option<int>("--count") {
            Description = "放入鸡蛋的数量（默认 1）",
            DefaultValueFactory = _ => 1,
        };
        var cmd = new Command("put-egg", "往冰箱里放入鸡蛋") { countOpt };
        cmd.SetAction(ctx => {
            var count = ctx.GetValue(countOpt);
            var eggs = _fridge.GetOrThrow<int>("eggs");
            var capacity = _fridge.GetOrThrow<int>("capacity");

            var newEggs = eggs + count;
            if (newEggs > capacity) {
                ctx.InvocationConfiguration.Output.WriteLine(
                    $"冰箱容量不足！当前 {eggs}/{capacity}，无法再放入 {count} 个鸡蛋。");
                return;
            }

            _fridge.Upsert("eggs", newEggs);
            _ = _repo.Commit(_fridge).Value;
            ctx.InvocationConfiguration.Output.WriteLine(
                $"放入 {count} 个鸡蛋 🥚 → 冰箱现有 {newEggs}/{capacity}");
        });
        return cmd;
    }

    // ─────────────────────────────────────────
    // get-egg
    // ─────────────────────────────────────────

    private static Command BuildGetEggCommand() {
        var countOpt = new Option<int>("--count") {
            Description = "取出鸡蛋的数量（默认 1）",
            DefaultValueFactory = _ => 1,
        };
        var cmd = new Command("get-egg", "从冰箱里取出鸡蛋") { countOpt };
        cmd.SetAction(ctx => {
            var count = ctx.GetValue(countOpt);
            var eggs = _fridge.GetOrThrow<int>("eggs");
            var capacity = _fridge.GetOrThrow<int>("capacity");

            if (eggs < count) {
                ctx.InvocationConfiguration.Output.WriteLine(
                    $"冰箱里鸡蛋不够！当前 {eggs}/{capacity}，无法取出 {count} 个鸡蛋。");
                return;
            }

            var newEggs = eggs - count;
            _fridge.Upsert("eggs", newEggs);
            _ = _repo.Commit(_fridge).Value;
            ctx.InvocationConfiguration.Output.WriteLine(
                $"取出 {count} 个鸡蛋 🍳 → 冰箱剩余 {newEggs}/{capacity}");
        });
        return cmd;
    }

    // ─────────────────────────────────────────
    // status
    // ─────────────────────────────────────────

    private static Command BuildStatusCommand() {
        var cmd = new Command("status", "查看冰箱当前状态");
        cmd.SetAction(ctx => {
            var eggs = _fridge.GetOrThrow<int>("eggs");
            var capacity = _fridge.GetOrThrow<int>("capacity");
            var output = ctx.InvocationConfiguration.Output;

            output.WriteLine($"🥚 鸡蛋: {eggs}/{capacity}");
            output.WriteLine($"📦 可用空间: {capacity - eggs}");
            if (eggs == 0) {
                output.WriteLine("💡 冰箱是空的，试试 put-egg 吧！");
            }
            else if (eggs >= capacity) {
                output.WriteLine("💡 冰箱满了，试试 get-egg 吧！");
            }
        });
        return cmd;
    }

    // ─────────────────────────────────────────
    // reset
    // ─────────────────────────────────────────

    private static Command BuildResetCommand() {
        var cmd = new Command("reset", "重置冰箱状态");
        cmd.SetAction(ctx => {
            _fridge.Upsert("eggs", 0);
            _ = _repo.Commit(_fridge).Value;
            ctx.InvocationConfiguration.Output.WriteLine("冰箱已重置 —— 空空如也！");
        });
        return cmd;
    }
}
