﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
        private readonly INssBuiltInFunctions _builtIns;

        private readonly List<ThreadContext> _threads;
        private readonly List<ThreadContext> _activeThreads;
        private readonly List<ThreadContext> _suspendedThreads;
        private readonly Queue<ThreadContext> _idleThreads;

        private readonly Stopwatch _timer;

        private uint _nextThreadId;

        private TimeSpan _totalElapsed = TimeSpan.Zero;

        public NSScriptInterpreter(IScriptLocator scriptLocator, INssBuiltInFunctions builtIns)
        {
            _builtIns = builtIns;
            _execVisitor = new ExecutingVisitor();

            _threads = new List<ThreadContext>();
            _activeThreads = new List<ThreadContext>();
            _suspendedThreads = new List<ThreadContext>();
            _idleThreads = new Queue<ThreadContext>();

            Session = new NSScriptSession(scriptLocator);
            Globals = new VariableTable();

            _builtinsDispatchTable = new Dictionary<string, Action<ArgumentStack>>
            {
                ["Wait"] = Wait,
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
                ["SetLoopPoint"] = SetLoopPoint,
                ["DisplayDialogue"] = DisplayDialogue
            };

            PredefinedConstants.Preload();

            Status = NSScriptInterpreterStatus.Idle;
            _timer = new Stopwatch();
        }

        public VariableTable Globals { get; }
        public NSScriptSession Session { get; }
        public NSScriptInterpreterStatus Status { get; private set; }

        public event EventHandler<BuiltInFunctionCall> BuiltInCallScheduled;

        public void CreateThread(Module module, Statement entryPoint)
        {
            uint id = _nextThreadId++;
            var thread = new ThreadContext(id, module, entryPoint, Globals);
            _threads.Add(thread);
            _activeThreads.Add(thread);
        }

        public void CreateThread(Module module) => CreateThread(module, module.MainChapter.Body);
        public void CreateThread(string moduleName) => CreateThread(Session.GetModule(moduleName));


        private Queue<uint> _threadsToResume = new Queue<uint>();

        public TimeSpan Run(TimeSpan timeQuota)
        {
            foreach (var suspendedThread in _suspendedThreads)
            {
                if (suspendedThread.SleepCounter >= suspendedThread.SleepTimeout)
                {
                    //ResumeThread(suspendedThread.Id);
                    _threadsToResume.Enqueue(suspendedThread.Id);
                }
            }

            while (_threadsToResume.Count > 0)
            {
                ResumeThread(_threadsToResume.Dequeue());
            }

            if (_activeThreads.Count == 0)
            {
                return TimeSpan.Zero;
            }

            Status = NSScriptInterpreterStatus.Running;
            _timer.Restart();
            while (Status == NSScriptInterpreterStatus.Running && _threads.Count > 0)
            {
                while (_execVisitor.PendingBuiltInCalls.Count > 0)
                {
                    if (IsApproaching(_timer.Elapsed, timeQuota))
                    {
                        goto exit;
                    }

                    var call = _execVisitor.PendingBuiltInCalls.Dequeue();
                    DispatchBuiltInCall(call);
                }

                while (_idleThreads.Count > 0)
                {
                    var thread = _idleThreads.Dequeue();
                    _activeThreads.Remove(thread);
                    _threads.Remove(thread);
                }

                if (_activeThreads.Count == 0)
                {
                    break;
                }

                foreach (var thread in _activeThreads)
                {
                    if (IsApproaching(_timer.Elapsed, timeQuota))
                    {
                        goto exit;
                    }

                    _execVisitor.Tick(thread);
                    if (thread.DoneExecuting)
                    {
                        _idleThreads.Enqueue(thread);
                    }
                }

                while (_execVisitor.PendingBuiltInCalls.Count > 0)
                {
                    if (IsApproaching(_timer.Elapsed, timeQuota))
                    {
                        goto exit;
                    }

                    var call = _execVisitor.PendingBuiltInCalls.Dequeue();
                    DispatchBuiltInCall(call);
                }
            }

            exit:
            _timer.Stop();
            _totalElapsed += _timer.Elapsed;

            foreach (var suspendedThread in _suspendedThreads)
            {
                suspendedThread.SleepCounter += _timer.Elapsed;
            }

            return _timer.Elapsed;
        }

        private static bool IsApproaching(TimeSpan elapsedTime, TimeSpan timeQuota)
        {
            if (elapsedTime >= timeQuota)
            {
                return true;
            }

            return (timeQuota - elapsedTime).TotalMilliseconds <= 2.0f;
        }

        public void Suspend()
        {
            Status = NSScriptInterpreterStatus.Suspended;
        }

        public void SuspendThread(uint threadId)
        {
            SuspendThread(threadId, TimeSpan.MaxValue);
        }

        public void SuspendThread(uint threadId, TimeSpan timeout)
        {
            if (threadId >= _threads.Count)
            {
                throw new ArgumentOutOfRangeException($"There is no thread with ID {threadId}.");
            }

            var thread = _threads[(int)threadId];
            thread.Suspend(timeout);
            _activeThreads.Remove(thread);
            _suspendedThreads.Add(thread);

            //SuspendThread(threadId, timeout);
            //Task.Delay(timeout).ContinueWith(x => ResumeThread(threadId), TaskContinuationOptions.ExecuteSynchronously);
        }

        public void ResumeThread(uint threadId)
        {
            if (threadId >= _threads.Count)
            {
                throw new ArgumentOutOfRangeException($"There is no thread with ID {threadId}.");
            }

            var thread = _threads[(int)threadId];
            if (thread != null)
            {
                thread.Resume();
                _activeThreads.Add(thread);
                _suspendedThreads.Remove(thread);
            }
        }

        public void DispatchBuiltInCall(BuiltInFunctionCall functionCall)
        {
            //while (_execVisitor.PendingBuiltInCalls.Count > 0 && Status != NSScriptInterpreterStatus.Suspended)
            //{
            Action<ArgumentStack> handler;
            _builtinsDispatchTable.TryGetValue(functionCall.FunctionName, out handler);
            BuiltInCallScheduled?.Invoke(this, functionCall);
            handler?.Invoke(functionCall.MutableArguments);
            //}
        }

        private void DisplayDialogue(ArgumentStack args)
        {
            var text = args.PopString();
            _builtIns.DisplayDialogue(text);
        }

        private void Wait(ArgumentStack args)
        {
            TimeSpan delay = args.PopTimeSpan();
            _builtIns.Delay(delay);
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
            string entityName = args.PopString();
            string alias = args.PopString();

            _builtIns.SetAlias(entityName, alias);
        }

        private void Request(ArgumentStack args)
        {
            string entityName = args.PopString();
            NssEntityAction action = args.PopNssAction();

            _builtIns.Request(entityName, action);
        }

        private void Delete(ArgumentStack args)
        {
            string entityName = args.PopString();
            _builtIns.RemoveEntity(entityName);
        }

        private void CreateTexture(ArgumentStack args)
        {
            string entityName = args.PopString();
            int priority = args.PopInt();
            NssCoordinate x = args.PopCoordinate();
            NssCoordinate y = args.PopCoordinate();
            string fileOrEntityName = args.PopString();

            _builtIns.AddTexture(entityName, priority, x, y, fileOrEntityName);
        }

        private void CreateSound(ArgumentStack args)
        {
            string entityName = args.PopString();
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
            _builtIns.LoadAudio(entityName, kind, fileName);
        }

        private void CreateColor(ArgumentStack args)
        {
            string entityName = args.PopString();
            int priority = args.PopInt();
            NssCoordinate x = args.PopCoordinate();
            NssCoordinate y = args.PopCoordinate();
            int width = args.PopInt();
            int height = args.PopInt();
            NssColor color = args.PopColor();

            _builtIns.AddRectangle(entityName, priority, x, y, width, height, color);
        }

        private void SetVolume(ArgumentStack args)
        {
            string entityName = args.PopString();
            TimeSpan duration = args.PopTimeSpan();
            int volume = args.PopInt();

            _builtIns.SetVolume(entityName, duration, volume);
        }

        private void Fade(ArgumentStack args)
        {
            string entityName = args.PopString();
            TimeSpan duration = args.PopTimeSpan();
            int opacity = args.PopInt();

            // Unknown. Usually null.
            args.Pop();

            bool wait = args.PopBool();
            _builtIns.Fade(entityName, duration, opacity, wait);
        }

        private void CreateWindow(ArgumentStack args)
        {
            string entityName = args.PopString();
            int priority = args.PopInt();
            NssCoordinate x = args.PopCoordinate();
            NssCoordinate y = args.PopCoordinate();
            int width = args.PopInt();
            int height = args.PopInt();

            _builtIns.CreateDialogueBox(entityName, priority, x, y, width, height);
        }

        private void WaitText(ArgumentStack args)
        {
            string entityName = args.PopString();
            TimeSpan time = args.PopTimeSpan();

            uint threadId = _builtIns.CallingThreadId;
            var thread = _threads[(int)threadId];

            thread.PushContinuation(_execVisitor.CurrentDialogueBlock);

            _builtIns.WaitText(entityName, time);
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
            string entityName = args.PopString();
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

            _builtIns.ToggleLooping(entityName, looping);
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
        private readonly ExpressionFlattener _exprFlattener;
        private readonly ExpressionReducer _exprReducer;
        private readonly RecursiveExpressionEvaluator _recursiveEvaluator;

        private ThreadContext _threadContext;

        public ExecutingVisitor()
        {
            _exprFlattener = new ExpressionFlattener();
            _exprReducer = new ExpressionReducer();
            _recursiveEvaluator = new RecursiveExpressionEvaluator();
            PendingBuiltInCalls = new Queue<BuiltInFunctionCall>();
        }

        private Frame CurrentFrame => _threadContext.CurrentFrame;
        public Queue<BuiltInFunctionCall> PendingBuiltInCalls { get; }
        public DialogueBlock CurrentDialogueBlock { get; private set; }

        public void Tick(ThreadContext context)
        {
            _threadContext = context;

            Frame prevFrame = context.CurrentFrame;
            Visit(context.CurrentNode);

            if (prevFrame == CurrentFrame)
            {
                context.Advance();
            }
        }

        private ConstantValue EvaluateTrivial(Expression expression)
        {
            return _recursiveEvaluator.EvaluateExpression(expression, _threadContext.CurrentFrame);
        }

        private bool Eval(Expression expression, out ConstantValue result)
        {
            //result = EvaluateTrivial(expression);
            //return true;

            if (CurrentFrame.EvaluationStack.Count == 0)
            {
                _exprFlattener.Flatten(expression, CurrentFrame.OperandStack, CurrentFrame.OperationStack);
            }

            _exprReducer.CurrentFrame = _threadContext.CurrentFrame;
            while (CurrentFrame.OperationStack.Count > 0)
            {
                var operation = CurrentFrame.OperationStack.Pop();
                var opCategory = OperationInfo.GetCategory(operation);
                switch (opCategory)
                {
                    case OperationCategory.None:
                        var operand = CurrentFrame.OperandStack.Pop();
                        CurrentFrame.EvaluationStack.Push(operand);
                        continue;

                    case OperationCategory.Unary:
                        operand = CurrentFrame.EvaluationStack.Pop();
                        var intermediateResult = _exprReducer.ApplyUnaryOperation(operand, operation);

                        CurrentFrame.EvaluationStack.Push(intermediateResult);
                        break;

                    case OperationCategory.Binary:
                        var leftOperand = CurrentFrame.EvaluationStack.Pop();
                        if (leftOperand.Kind == SyntaxNodeKind.FunctionCall)
                        {
                            PrepareFunctionCall(leftOperand as FunctionCall);
                            result = default(ConstantValue);
                            return false;
                        }

                        var rightOperand = CurrentFrame.EvaluationStack.Pop();
                        intermediateResult = _exprReducer.ApplyBinaryOperation(_exprReducer.ReduceExpression(leftOperand), operation, _exprReducer.ReduceExpression(rightOperand));

                        CurrentFrame.EvaluationStack.Push(intermediateResult);
                        break;

                    case OperationCategory.Assignment:
                        var targetVariable = CurrentFrame.EvaluationStack.Pop() as Variable;
                        var value = _exprReducer.ReduceExpression(CurrentFrame.EvaluationStack.Pop());

                        string targetName = targetVariable.Name.SimplifiedName;
                        _threadContext.CurrentFrame.Globals[targetName] = value;
                        CurrentFrame.EvaluationStack.Push(value);
                        break;
                }
            }

            result = (ConstantValue)CurrentFrame.EvaluationStack.Pop();
            return true;
        }

        public override void VisitExpressionStatement(ExpressionStatement expressionStatement)
        {
            var expr = expressionStatement.Expression;

            // Fast path for simple function calls.
            if (expr is FunctionCall fc)
            {
                PrepareFunctionCall(fc);
                return;
            }

            Eval(expr, out var _);
        }

        public override void VisitIfStatement(IfStatement ifStatement)
        {
            if (Eval(ifStatement.Condition, out var condition))
            {
                if (condition == ConstantValue.True)
                {
                    _threadContext.PushContinuation(ifStatement.IfTrueStatement);
                }
                else if (ifStatement.IfFalseStatement != null)
                {
                    _threadContext.PushContinuation(ifStatement.IfFalseStatement);
                }
            }
        }

        public override void VisitWhileStatement(WhileStatement whileStatement)
        {
            if (Eval(whileStatement.Condition, out var condition))
            {
                if (condition.RawValue is 1)
                {
                    _threadContext.PushContinuation(whileStatement.Body);
                }
            }
        }

        public override void VisitDialogueBlock(DialogueBlock dialogueBlock)
        {
            CurrentFrame.Globals["SYSTEM_present_preprocess"] = new ConstantValue(dialogueBlock.BoxName);
            CurrentFrame.Globals["SYSTEM_present_text"] = new ConstantValue(dialogueBlock.Identifier);

            CurrentDialogueBlock = dialogueBlock;
        }

        public override void VisitPXmlString(PXmlString pxmlString)
        {
            var arg = new ConstantValue(pxmlString.Text);
            ScheduleBuiltInCall("DisplayDialogue", new ArgumentStack(ImmutableArray.Create(arg)));
        }

        public override void VisitBlock(Block block)
        {
            _threadContext.PushContinuation(block.Statements);
        }

        public void PrepareFunctionCall(FunctionCall functionCall)
        {
            string name = functionCall.TargetFunctionName.SimplifiedName;
            Function target;
            if (!_threadContext.CurrentModule.TryGetFunction(name, out target))
            {
                ScheduleBuiltInCall(functionCall);
                return;
            }

            var arguments = new VariableTable();
            for (int i = 0; i < target.Parameters.Length; i++)
            {
                ParameterReference param = target.Parameters[i];
                ConstantValue arg = EvaluateTrivial(functionCall.Arguments[i]);
                arguments[param.ParameterName.SimplifiedName] = arg;
            }

            _threadContext.PushContinuation(target.Body, arguments);
        }

        private void ScheduleBuiltInCall(FunctionCall functionCall)
        {
            string name = functionCall.TargetFunctionName.SimplifiedName;
            var args = new ArgumentStack(functionCall.Arguments.Select(EvaluateTrivial).Reverse());
            ScheduleBuiltInCall(name, args);
        }

        private void ScheduleBuiltInCall(string functionName, ArgumentStack arguments)
        {
            PendingBuiltInCalls.Enqueue(new BuiltInFunctionCall(functionName, arguments, _threadContext.Id));
        }
    }
}
