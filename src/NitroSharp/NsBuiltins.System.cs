﻿using System;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Execution;
using NitroSharp.Content;
using System.Collections.Generic;
using System.Linq;
using NitroSharp.Utilities;

namespace NitroSharp
{
    internal sealed partial class NsBuiltins : EngineImplementation
    {
        private World _world;
        private readonly Game _game;

        private readonly Queue<string> _entitiesToRemove = new Queue<string>();

        public NsBuiltins(Game game)
        {
            _game = game;
        }

        public void SetWorld(World gameWorld) => _world = gameWorld;

        private ContentManager Content => _game.Content;

        public override void CreateChoice(string entityName)
        {
            _world.CreateChoice(entityName);
        }

        private void SuspendMainThread()
        {
            Interpreter.SuspendThread(MainThread);
        }

        private void ResumeMainThread()
        {
            Interpreter.ResumeThread(MainThread);
        }

        public override void SetAlias(string entityName, string alias)
        {
            if (entityName != alias)
            {
                _world.SetAlias(alias, entityName);
                _world.SetAlias(entityName, alias);
            }
        }

        public override void RemoveEntity(string entityName)
        {
            foreach ((Entity entity, string name) in _world.Query(entityName))
            {
                var table = _world.GetTable<EntityTable>(entity);
                if (!table.IsLocked.GetValue(entity))
                {
                    _entitiesToRemove.Enqueue(name);
                    var attachedThread = Interpreter.Threads.FirstOrDefault(x => entityName.StartsWith(x.Name));
                    if (attachedThread != null)
                    {
                        Interpreter.TerminateThread(attachedThread);
                    }
                }
            }

            while (_entitiesToRemove.Count > 0)
            {
                _world.RemoveEntity(_entitiesToRemove.Dequeue());
            }
        }

        public override void Delay(TimeSpan delay)
        {
            Interpreter.SuspendThread(CurrentThread, delay);
        }

        public override void WaitForInput()
        {
            //if (_dialogueState.DialogueLine?.IsEmpty == true)
            //{
            //    return;
            //}

            Interpreter.SuspendThread(CurrentThread);
        }

        public override void WaitForInput(TimeSpan timeout)
        {
            Interpreter.SuspendThread(CurrentThread, timeout);
        }

        public override void CreateThread(string name, string target)
        {
            bool startImmediately = _world.Query(name + "*").Any();
            ThreadContext thread = Interpreter.CreateThread(name, target, startImmediately);
            Entity threadEntity = _world.CreateThreadEntity(name, thread.EntryModule, target);

            Entity parentEntity = default;
            int idxSlash = name.IndexOf('/');
            if (idxSlash > 0)
            {
                string parentEntityName = name.Substring(0, idxSlash);
                _world.TryGetEntity(parentEntityName, out parentEntity);
            }

            if (parentEntity.IsValid)
            {
                if (parentEntity.Kind == EntityKind.Choice)
                {
                    if (name.Contains("MouseOver"))
                    {
                        _world.Choices.MouseOverThread.Set(parentEntity, threadEntity);
                    }
                    else if (name.Contains("MouseLeave"))
                    {
                        _world.Choices.MouseLeaveThread.Set(parentEntity, threadEntity);
                    }
                }
            }
        }

        public override void Request(string entityName, NsEntityAction action)
        {
            foreach ((Entity entity, string name) in _world.Query(entityName))
            {
                RequestCore(entity, name, action);
            }
        }

        private void RequestCore(Entity entity, string entityName, NsEntityAction action)
        {
            EntityTable table = _world.GetTable<EntityTable>(entity);
            switch (action)
            {
                case NsEntityAction.Lock:
                    table.IsLocked.Set(entity, true);
                    break;
                case NsEntityAction.Unlock:
                    table.IsLocked.Set(entity, false);
                    break;

                case NsEntityAction.Start:
                    if (Interpreter.TryGetThread(entityName, out var thread))
                    {
                        Interpreter.ResumeThread(thread);
                    }
                    break;

                case NsEntityAction.Stop:
                    if (Interpreter.TryGetThread(entityName, out thread))
                    {
                        Interpreter.TerminateThread(thread);
                    }
                    break;
            }
        }

        public override ConstantValue FormatString(string format, object[] args)
        {
            string s = CRuntime.sprintf(format, args);
            return ConstantValue.Create(s);
        }
    }
}
