﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace SciAdvNet.NSScript.Execution
{
    public enum NSScriptInterpreterStatus
    {
        Running,
        Suspended,
        Idle
    }

    public class NSScriptInterpreter
    {
        private readonly Dictionary<string, Action<ArgumentStack>> _builtinsDispatchTable;

        private readonly ExecutingVisitor _execVisitor;
        private readonly INssBuiltInMethods _builtIns;
        private readonly List<ThreadContext> _threads;

        private uint _nextThreadId;

        public NSScriptInterpreter(IScriptLocator scriptLocator, INssBuiltInMethods builtIns)
        {
            _builtIns = builtIns;
            _execVisitor = new ExecutingVisitor(builtIns);
            _threads = new List<ThreadContext>();

            Session = new NSScriptSession(scriptLocator);
            Globals = new VariableTable();

            _builtinsDispatchTable = new Dictionary<string, Action<ArgumentStack>>(StringComparer.OrdinalIgnoreCase)
            {
                ["WaitKey"] = WaitKey,
                ["Request"] = Request,
                ["Delete"] = Delete,
                ["SetAlias"] = SetAlias,
                ["CreateColor"] = CreateColor,
                ["CreateTexture"] = CreateTexture,
                ["CreateSound"] = CreateSound,
                ["Fade"] = Fade,
                ["SetVolume"] = SetVolume,
                ["CreateWindow"] = CreateWindow,
                ["LoadText"] = LoadText,
                ["WaitText"] = WaitText,
                ["SetLoop"] = SetLoop,
                ["SetLoopPoint"] = SetLoopPoint
            };

            Status = NSScriptInterpreterStatus.Idle;
        }

        public VariableTable Globals { get; }
        public NSScriptSession Session { get; }
        public NSScriptInterpreterStatus Status { get; private set; }
        public ImmutableArray<BuiltInMethodCall> PendingBuiltInCalls => _execVisitor.PendingBuiltInCalls.ToImmutableArray();

        //public event EventHandler<BuiltInMethodCall>

        public void CreateMicrothread(Module module, Statement entryPoint)
        {
            uint id = _nextThreadId++;
            var thread = new ThreadContext(id, module, entryPoint, Globals);
            _threads.Add(thread);
        }

        public void CreateMicrothread(Module module) => CreateMicrothread(module, module.MainChapter.Body);
        public void CreateMicrothread(string moduleName) => CreateMicrothread(Session.GetModule(moduleName));

        public void Run()
        {
            Status = NSScriptInterpreterStatus.Running;

            var idleThreads = new Queue<ThreadContext>();
            while (Status == NSScriptInterpreterStatus.Running && _threads.Count > 0)
            {
                while (idleThreads.Count > 0)
                {
                    _threads.Remove(idleThreads.Dequeue());
                }

                var activeThreads = _threads.Where(x => x.Suspended == false).ToArray();
                if (activeThreads.Length == 0)
                {
                    return;
                }

                foreach (var thread in activeThreads)
                {
                    _execVisitor.ExecuteNode(thread);
                    if (thread.DoneExecuting)
                    {
                        idleThreads.Enqueue(thread);
                    }
                }

                DispatchPendingBuiltInCalls();
            }
        }

        public void Suspend()
        {
            Status = NSScriptInterpreterStatus.Suspended;
        }

        public void SuspendThread(uint threadId)
        {
            _threads.FirstOrDefault(x => x.Id == threadId)?.Suspend();
        }

        public void SuspendThread(uint threadId, TimeSpan timeout)
        {
            SuspendThread(threadId);
            Task.Delay(timeout).ContinueWith(x => ResumeThread(threadId), TaskContinuationOptions.ExecuteSynchronously);
        }

        public void ResumeThread(uint threadId)
        {
            _threads.FirstOrDefault(x => x.Id == threadId)?.Resume();
        }

        public void DispatchPendingBuiltInCalls()
        {
            while (_execVisitor.PendingBuiltInCalls.Count > 0 && Status != NSScriptInterpreterStatus.Suspended)
            {
                var methodCall = _execVisitor.PendingBuiltInCalls.Dequeue();

                Console.WriteLine(methodCall);

                Action<ArgumentStack> handler;
                _builtinsDispatchTable.TryGetValue(methodCall.MethodName, out handler);
                handler?.Invoke(methodCall.MutableArguments);
            }
        }

        private void Wait(ArgumentStack args)
        {
            TimeSpan delay = args.PopTimeSpan();
            _builtIns.Wait(delay);
        }

        private void WaitKey(ArgumentStack args)
        {
            if (args.Count > 0)
            {
                TimeSpan timeout = args.PopTimeSpan();
                _builtIns.WaitForInput(timeout);
            }
            else
            {
                _builtIns.WaitForInput();
            }
        }

        private void SetAlias(ArgumentStack args)
        {
            string symbol = args.PopString();
            string alias = args.PopString();

            _builtIns.SetAlias(symbol, alias);
        }

        private void Request(ArgumentStack args)
        {
            string symbol = args.PopString();
            NssAction action;
            string strAction = args.PopString();
            switch (strAction.ToUpperInvariant())
            {
                case "LOCK":
                    action = NssAction.Lock;
                    break;

                case "PLAY":
                    action = NssAction.Play;
                    break;

                case "UNLOCK":
                    action = NssAction.Unlock;
                    break;

                default:
                    action = NssAction.Other;
                    break;
            }

            _builtIns.Request(symbol, action);
        }

        private void Delete(ArgumentStack args)
        {
            string symbol = args.PopString();
            _builtIns.RemoveEntity(symbol);
        }

        private void CreateTexture(ArgumentStack args)
        {
            string symbol = args.PopString();
            int zLevel = args.PopInt();
            NssCoordinate x = args.PopCoordinate();
            NssCoordinate y = args.PopCoordinate();
            string fileName = args.PopString();

            _builtIns.AddTexture(symbol, zLevel, x, y, fileName);
        }

        private void CreateSound(ArgumentStack args)
        {
            string symbol = args.PopString();
            string strAudioKind = args.PopString();
            AudioKind kind;
            switch (strAudioKind)
            {
                case "SE":
                    kind = AudioKind.SoundEffect;
                    break;

                case "BGM":
                default:
                    kind = AudioKind.BackgroundMusic;
                    break;
            }

            string fileName = args.PopString();
            _builtIns.LoadAudio(symbol, kind, fileName);
        }

        private void CreateColor(ArgumentStack args)
        {
            string symbol = args.PopString();
            int zLevel = args.PopInt();
            NssCoordinate x = args.PopCoordinate();
            NssCoordinate y = args.PopCoordinate();
            int width = args.PopInt();
            int height = args.PopInt();
            NssColor color = args.PopColor();

            _builtIns.AddRectangle(symbol, zLevel, x, y, width, height, color);
        }

        private void SetVolume(ArgumentStack args)
        {
            string symbol = args.PopString();
            TimeSpan duration = args.PopTimeSpan();
            int volume = args.PopInt();

            _builtIns.SetVolume(symbol, duration, volume);
        }

        private void Fade(ArgumentStack args)
        {
            string symbol = args.PopString();
            TimeSpan duration = args.PopTimeSpan();
            int opacity = args.PopInt();

            // Unknown. Usually null.
            args.Pop();

            bool wait = args.PopBool();
            _builtIns.Fade(symbol, duration, opacity, wait);
        }

        private void CreateWindow(ArgumentStack args)
        {
            string symbol = args.PopString();
            int zLevel = args.PopInt();
            NssCoordinate x = args.PopCoordinate();
            NssCoordinate y = args.PopCoordinate();
            int width = args.PopInt();
            int height = args.PopInt();

            _builtIns.CreateDialogueBox(symbol, zLevel, x, y, width, height);
        }

        private void WaitText(ArgumentStack args)
        {
            string symbol = args.PopString();
            TimeSpan time = args.PopTimeSpan();

            _builtIns.WaitText(symbol, time);
        }

        private void LoadText(ArgumentStack args)
        {
            string unk = args.PopString();
            string boxName = args.PopString();
            string someStr = args.PopString();

            int maxWidth = args.PopInt();
            int maxHeight = args.PopInt();
            int letterSpacing = args.PopInt();
            int lineSpacing = args.PopInt();
        }

        private void SetFont(ArgumentStack args)
        {
            string fontName = args.PopString();
            int size = args.PopInt();
            NssColor inColor = args.PopColor();
            NssColor outColor = args.PopColor();
            int fontWeight = args.PopInt();

            string strAlignment = args.PopString();
            //TextAlignment alignment;
            //switch (strAlignment.ToUpperInvariant())
            //{
            //    case "DOWN":
            //    default:
            //        alignment = TextAlignment.Bottom;
            //        break;
            //}
        }

        private void Move(ArgumentStack args)
        {
            string symbol = args.PopString();
            TimeSpan duration = args.PopTimeSpan();
            NssCoordinate targetX = args.PopCoordinate();
            NssCoordinate targetY = args.PopCoordinate();
            int unk = args.PopInt();
            bool wait = args.PopBool();
        }

        private void SetLoop(ArgumentStack args)
        {
            string entityName = args.PopString();
            bool looping = args.PopBool();

            _builtIns.SetLoop(entityName, looping);
        }

        private void SetLoopPoint(ArgumentStack args)
        {
            string entityName = args.PopString();
            TimeSpan loopStart = args.PopTimeSpan();
            TimeSpan loopEnd = args.PopTimeSpan();

            _builtIns.SetLoopPoint(entityName, loopStart, loopEnd);
        }
    }

    internal sealed class ExecutingVisitor : SyntaxVisitor
    {
        private readonly ExpressionEvaluator _exprEvaluator;
        private readonly INssBuiltInMethods _builtIns;

        private ThreadContext _threadContext;

        public ExecutingVisitor(INssBuiltInMethods builtIns)
        {
            _exprEvaluator = new ExpressionEvaluator();
            _builtIns = builtIns;
            PendingBuiltInCalls = new Queue<BuiltInMethodCall>();
        }

        public Queue<BuiltInMethodCall> PendingBuiltInCalls { get; }

        public void ExecuteNode(ThreadContext context)
        {
            _threadContext = context;
            Frame prevFrame = context.CurrentFrame;
            Visit(context.CurrentNode);
            if (prevFrame == context.CurrentFrame)
            {
                context.Advance();
            }
        }

        private ConstantValue Evaluate(Expression expression)
        {
            return _exprEvaluator.EvaluateExpression(expression, _threadContext.CurrentFrame);
        }

        public override void VisitExpressionStatement(ExpressionStatement expressionStatement)
        {
            Evaluate(expressionStatement.Expression);
        }

        public override void VisitIfStatement(IfStatement ifStatement)
        {
            if (Evaluate(ifStatement.Condition) == ConstantValue.True)
            {
                _threadContext.PushContinuation(ifStatement.IfTrueStatement);
            }
            else if (ifStatement.IfFalseStatement != null)
            {
                _threadContext.PushContinuation(ifStatement.IfFalseStatement);
            }
        }

        public override void VisitWhileStatement(WhileStatement whileStatement)
        {
            if (Evaluate(whileStatement.Condition) == ConstantValue.True)
            {
                _threadContext.PushContinuation(whileStatement.Body);
                _threadContext.PushContinuation(whileStatement);
            }
        }

        public override void VisitDialogueBlock(DialogueBlock dialogueBlock)
        {
            _threadContext.PushContinuation(dialogueBlock.Statements);
        }

        public override void VisitBlock(Block block)
        {
            _threadContext.PushContinuation(block.Statements);
        }

        public override void VisitDialogueLine(DialogueLine dialogueLine)
        {
            _builtIns.DisplayDialogue(dialogueLine);
        }

        public override void VisitMethodCall(MethodCall methodCall)
        {
            string name = methodCall.TargetMethodName.SimplifiedName;
            Method target;
            if (!_threadContext.CurrentModule.TryGetMethod(name, out target))
            {
                EnqueueBuiltIn(methodCall);
                return;
            }

            var arguments = new VariableTable();
            for (int i = 0; i < target.Parameters.Length; i++)
            {
                ParameterReference param = target.Parameters[i];
                ConstantValue arg = Evaluate(methodCall.Arguments[i]);
                arguments[param.ParameterName.SimplifiedName] = arg;
            }

            _threadContext.PushContinuation(target.Body, arguments);
        }

        private void EnqueueBuiltIn(MethodCall methodCall)
        {
            string name = methodCall.TargetMethodName.SimplifiedName;
            var args = new ArgumentStack(methodCall.Arguments.Select(Evaluate).Reverse());
            PendingBuiltInCalls.Enqueue(new BuiltInMethodCall(name, args, _threadContext.Id));
        }
    }
}
