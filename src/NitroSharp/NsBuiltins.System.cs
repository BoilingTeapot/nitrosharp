﻿using System;
using NitroSharp.NsScript;
using System.Collections.Generic;
using System.Linq;
using NitroSharp.Utilities;
using NitroSharp.NsScript.VM;
using NitroSharp.Content;
using System.Text;

namespace NitroSharp
{
    internal sealed partial class NsBuiltins : BuiltInFunctions
    {
        private World _world;
        private readonly Game _game;
        private readonly Logger _logger;
        private readonly Queue<string> _entitiesToRemove = new Queue<string>();
        private readonly Queue<Game.Message> _messageQueue = new Queue<Game.Message>();

        public NsBuiltins(Game game)
        {
            _game = game;
            _logger = game.Logger;
            _fontConfig = game.FontConfiguration;
        }

        private ContentManager Content => _game.Content;
        public Queue<Game.Message> MessagesForPresenter => _messageQueue;
        public string SelectedChoice { get; set; }

        public void SetWorld(World gameWorld) => _world = gameWorld;

        public override void CreateChoice(string entityName)
        {
            _world.CreateChoice(entityName);
        }

        public override string GetSelectedChoice()
        {
            return SelectedChoice;
        }

        public override void Select()
        {
            //_game.MessageQueue.Enqueue(new SelectChoiceMessage
            //{
            //    WaitingThread = CurrentThread
            //});

            Interpreter.SuspendThread(CurrentThread);
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
                _world.SetAlias(entityName, alias);
            }
        }

        public override void RemoveEntity(string entityName)
        {
            foreach ((Entity entity, string name) in QueryEntities(entityName))
            {
                var table = _world.GetTable<EntityTable>(entity);
                if (!table.IsLocked.GetValue(entity))
                {
                    _entitiesToRemove.Enqueue(name);
                    ThreadContext attachedThread = Interpreter.Threads
                        .FirstOrDefault(x => entityName.StartsWith(x.Name));
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

        private readonly StringBuilder _logMessage = new StringBuilder();

        private EntityQueryResult QueryEntities(string query)
        {
            EntityQueryResult eqr = _world.Query(query);
            if (eqr.IsEmpty)
            {
                _logMessage.Clear();
                _logMessage.Append("Game object query yielded no results: ");
                _logMessage.Append("'");
                _logMessage.Append(eqr.Query);
                _logMessage.Append("'");
                _logger.LogWarning(_logMessage);
            }
            return eqr;
        }

        public override void Delay(TimeSpan delay)
        {
            Interpreter.SuspendThread(CurrentThread, delay);
        }

        public override void WaitForInput()
        {
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
            var info = new InterpreterThreadInfo(name, thread.EntryModule, target);
            Entity threadEntity = _world.CreateThreadEntity(info);
            Entity parent = _world.Threads.Parents.GetValue(threadEntity);
            if (parent.IsValid && parent.Kind == EntityKind.Choice)
            {
                var parsedName = new EntityName(name);
                ChoiceTable choices = _world.Choices;
                switch (parsedName.MouseState)
                {
                    case Interactivity.MouseState.Over:
                        choices.MouseOverThread.Set(parent, threadEntity);
                        break;
                    case Interactivity.MouseState.Leave:
                        choices.MouseLeaveThread.Set(parent, threadEntity);
                        break;
                }
            }
        }

        public override void Request(string entityName, NsEntityAction action)
        {
            foreach ((Entity entity, string name) in QueryEntities(entityName))
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
                    if (Interpreter.TryGetThread(entityName, out ThreadContext thread))
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

                case NsEntityAction.SetAdditiveBlend:
                    _world.GetEntityStruct<RenderItem>(entity).AsMutable()
                        .BlendMode = Graphics.BlendMode.Additive;
                    break;
                case NsEntityAction.SetSubtractiveBlend:
                    _world.GetEntityStruct<RenderItem>(entity).AsMutable()
                        .BlendMode = Graphics.BlendMode.Subtractive;
                    break;
                case NsEntityAction.SetMultiplicativeBlend:
                    _world.GetEntityStruct<RenderItem>(entity).AsMutable()
                        .BlendMode = Graphics.BlendMode.Multiplicative;
                    break;
            }
        }

        public override ConstantValue FormatString(string format, object[] args)
        {
            string s = CRuntime.sprintf(format, args);
            return ConstantValue.String(s);
        }
    }
}
