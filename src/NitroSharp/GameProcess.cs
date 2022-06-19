﻿using System;
using System.Collections.Generic;
using System.Linq;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;
using NitroSharp.Saving;
using NitroSharp.Text;
using Veldrid;

namespace NitroSharp
{
    internal enum WaitCondition
    {
        None,
        UserInput,
        MoveCompleted,
        ZoomCompleted,
        RotateCompleted,
        FadeCompleted,
        BezierMoveCompleted,
        TransitionCompleted,
        EntityIdle,
        LineRead
    }

    internal readonly record struct WaitOperation(
        NsScriptThread Thread,
        WaitCondition Condition,
        EntityQuery? EntityQuery)
    {
        public WaitOperation(NsScriptProcess vmProcess, in WaitOperationSaveData saveData)
            : this(vmProcess.GetThread(saveData.ThreadId), saveData.WaitCondition, null)
        {
            if (saveData.EntityQuery is { } entityQuery)
            {
                EntityQuery = new EntityQuery(entityQuery);
            }
        }

        public void Deconstruct(out WaitCondition condition, out EntityQuery? query)
        {
            condition = Condition;
            query = EntityQuery;
        }

        public WaitOperationSaveData ToSaveData() => new()
        {
            ThreadId = Thread.Id,
            EntityQuery = EntityQuery?.Value,
            WaitCondition = Condition
        };
    }

    internal sealed class GameProcess
    {
        private readonly Dictionary<uint, WaitOperation> _waitOperations = new();
        private readonly Queue<NsScriptThread> _threadsToResume = new();

        public GameProcess(NsScriptProcess vmProcess, FontSettings fontSettings)
        {
            VmProcess = vmProcess;
            World = new World();
            FontSettings = fontSettings;
        }

        public GameProcess(NsScriptProcess vmProcess, World world, FontSettings fontSettings)
        {
            VmProcess = vmProcess;
            World = world;
            FontSettings = fontSettings;
        }

        public GameProcess(
            GameContext ctx,
            in GameProcessSaveData saveData,
            IReadOnlyList<Texture> standaloneTextures)
        {
            FontSettings = saveData.FontSettings;
            VmProcess = ctx.VM.RestoreProcess(saveData.VmProcessDump);

            var loadingCtx = new GameLoadingContext
            {
                Process = this,
                GameContext = ctx,
                StandaloneTextures = standaloneTextures,
                Rendering = ctx.RenderContext,
                Content = ctx.Content,
                VM = ctx.VM,
                Backlog = ctx.Backlog
            };

            World = World.Load(saveData.World, loadingCtx);
            foreach (WaitOperationSaveData waitOp in saveData.WaitOperations)
            {
                _waitOperations[waitOp.ThreadId] = new WaitOperation(VmProcess, waitOp);
            }
        }

        public NsScriptProcess VmProcess { get; }
        public World World { get; }
        public FontSettings FontSettings { get; private set; }

        public void ChangeFontSettings(Func<FontSettings, FontSettings> mutateFunc)
        {
            FontSettings = mutateFunc(FontSettings);
        }

        public void Wait(
            NsScriptThread thread,
            WaitCondition condition,
            TimeSpan? timeout = null,
            EntityQuery? entityQuery = null)
        {
            VmProcess.VM.SuspendThread(thread, timeout);
            if (condition != WaitCondition.None)
            {
                _waitOperations[thread.Id] = new WaitOperation(thread, condition, entityQuery);
            }
        }

        public void ProcessWaitOperations(GameContext ctx)
        {
            foreach (WaitOperation wait in _waitOperations.Values)
            {
                if (wait.Thread.IsActive) { continue; }
                if (ShouldResume(wait, ctx))
                {
                    _threadsToResume.Enqueue(wait.Thread);
                }
            }

            while (_threadsToResume.TryDequeue(out NsScriptThread? thread))
            {
                VmProcess.VM.ResumeThread(thread);
                _waitOperations.Remove(thread.Id);
            }
        }

        public GameProcessSaveData Dump(GameSavingContext ctx) => new()
        {
            World = World.ToSaveData(ctx),
            WaitOperations = _waitOperations.Values
                .Select(x => x.ToSaveData())
                .ToArray(),
            VmProcessDump = VmProcess.Dump(),
            FontSettings = FontSettings
        };

        public void Dispose()
        {
            VmProcess.Terminate();
            World.Dispose();
        }

        private bool ShouldResume(in WaitOperation wait, GameContext ctx)
        {
            uint contextId = wait.Thread.Id;

            bool checkInput() => ctx.Advance || ctx.Skipping;

            bool checkIdle(EntityQuery query)
            {
                foreach (Entity entity in World.Query(contextId, query))
                {
                    if (!entity.IsIdle) { return false; }
                }

                return true;
            }

            bool checkAnim(EntityQuery query, AnimationKind anim)
            {
                foreach (RenderItem entity in World.Query<RenderItem>(contextId, query))
                {
                    if (entity.IsAnimationActive(anim)) { return false; }
                }

                return true;
            }

            bool checkLineRead(EntityQuery query)
            {
                if (ctx.Skipping) { return true; }
                foreach (DialoguePage page in World.Query<DialoguePage>(contextId, query))
                {
                    if (page.LineRead) { return true; }
                }

                return false;
            }

            return wait switch
            {
                (WaitCondition.UserInput, _) => checkInput(),
                (WaitCondition.EntityIdle, { } query) => checkIdle(query),
                (WaitCondition.FadeCompleted, { } query) => checkAnim(query, AnimationKind.Fade),
                (WaitCondition.MoveCompleted, { } query) => checkAnim(query, AnimationKind.Move),
                (WaitCondition.ZoomCompleted, { } query) => checkAnim(query, AnimationKind.Zoom),
                (WaitCondition.RotateCompleted, { } query) => checkAnim(query, AnimationKind.Rotate),
                (WaitCondition.BezierMoveCompleted, { } query) => checkAnim(query, AnimationKind.BezierMove),
                (WaitCondition.TransitionCompleted, { } query) => checkAnim(query, AnimationKind.Transition),
                (WaitCondition.LineRead, { } query) => checkLineRead(query),
                _ => false
            };
        }
    }

    [Persistable]
    internal readonly partial struct GameProcessSaveData
    {
        public NsScriptProcessDump VmProcessDump { get; init; }
        public WorldSaveData World { get; init; }
        public WaitOperationSaveData[] WaitOperations { get; init; }
        public FontSettings FontSettings { get; init; }
    }

    [Persistable]
    internal readonly partial struct WaitOperationSaveData
    {
        public uint ThreadId { get; init; }
        public WaitCondition WaitCondition { get; init; }
        public string? EntityQuery { get; init; }
    }
}
