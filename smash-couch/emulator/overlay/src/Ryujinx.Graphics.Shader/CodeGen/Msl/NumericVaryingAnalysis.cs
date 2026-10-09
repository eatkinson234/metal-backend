using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.StructuredIr;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;

namespace Ryujinx.Graphics.Shader.CodeGen.Msl
{
    /// <summary>
    /// Classifies a deliberately small subset of typed shader IR as numerical
    /// raster-interface data. Unsupported or ambiguous IR is refused.
    /// </summary>
    internal static class NumericVaryingAnalysis
    {
        private const int LocationCount = 32;
        private const int ComponentCount = 4;

        public static byte[] Analyze(
            StructuredProgramInfo info,
            CodeGenParameters parameters,
            IReadOnlyDictionary<IAstNode, HashSet<AggregateType>> emittedExpectedTypes,
            out IReadOnlyDictionary<string, int> refusalCounts)
        {
            Analyzer analyzer = new(info, parameters, emittedExpectedTypes);
            byte[] masks;

            try
            {
                masks = analyzer.Run();
            }
            catch (Exception)
            {
                // Analysis is advisory. Malformed or newly extended IR must not
                // make shader generation fail or turn an unknown into permission.
                analyzer.Refuse("analysis-failure");
                masks = new byte[LocationCount];
            }

            refusalCounts = new ReadOnlyDictionary<string, int>(analyzer.Refusals);
            return masks;
        }

        private sealed class Analyzer
        {
            private const int MaxNodes = 100_000;
            private const int MaxEdges = 250_000;
            private const int MaxAstDepth = 256;
            private const int MaxTraceDepth = 128;
            private const int MaxTraceStates = 50_000;

            private static readonly HashSet<Instruction> NumericFloatInstructions =
            [
                Instruction.Absolute,
                Instruction.Add,
                Instruction.Ceiling,
                Instruction.Clamp,
                Instruction.Cosine,
                Instruction.Ddx,
                Instruction.Ddy,
                Instruction.Divide,
                Instruction.ExponentB2,
                Instruction.Floor,
                Instruction.FusedMultiplyAdd,
                Instruction.LogarithmB2,
                Instruction.Maximum,
                Instruction.Minimum,
                Instruction.Modulo,
                Instruction.Multiply,
                Instruction.Negate,
                Instruction.ReciprocalSquareRoot,
                Instruction.Round,
                Instruction.Sine,
                Instruction.SquareRoot,
                Instruction.Subtract,
                Instruction.Truncate,
            ];

            private readonly StructuredProgramInfo _info;
            private readonly CodeGenParameters _parameters;
            private readonly IReadOnlyDictionary<IAstNode, HashSet<AggregateType>> _expectedTypes;
            private readonly ShaderDefinitions _definitions;
            private readonly Dictionary<string, int> _refusals = new(StringComparer.Ordinal);
            private readonly Dictionary<IAstNode, ExecutionPoint> _points = new(ReferenceComparer<IAstNode>.Instance);
            private readonly Dictionary<IAstNode, List<UseSite>> _uses = new(ReferenceComparer<IAstNode>.Instance);
            private readonly HashSet<IAstNode> _visitedNodes = new(ReferenceComparer<IAstNode>.Instance);
            private readonly HashSet<IAstNode> _activeNodes = new(ReferenceComparer<IAstNode>.Instance);
            private readonly Dictionary<int, List<AstOperation>> _producerStores = [];
            private readonly Dictionary<int, List<AstOperation>> _consumerLoads = [];
            private readonly HashSet<int> _aggregateInputLocations = [];
            private readonly HashSet<int> _aggregateOutputLocations = [];
            private readonly HashSet<int> _unsupportedInputLocations = [];
            private readonly HashSet<int> _unsupportedOutputLocations = [];
            // Completed successes only, scoped to this immutable program analysis.
            // A producer proof also depends on the exact lexical use site/type.
            private readonly Dictionary<ProducerTraceKey, TraceResult> _producerMemo = [];
            private readonly Dictionary<ConsumerTraceKey, ConsumerTrace> _consumerMemo = [];
            private readonly record struct ProducerTraceKey(IAstNode Node, Frame[] UseFrames, AggregateType ExpectedType);
            private readonly record struct ConsumerTraceKey(IAstNode Node, bool Transformed);
            private int _nodeCount;
            private int _edgeCount;
            private int _traceStates;
            private string _globalRefusal;

            public Dictionary<string, int> Refusals => _refusals;

            public Analyzer(
                StructuredProgramInfo info,
                CodeGenParameters parameters,
                IReadOnlyDictionary<IAstNode, HashSet<AggregateType>> emittedExpectedTypes)
            {
                _info = info;
                _parameters = parameters;
                _expectedTypes = emittedExpectedTypes;
                _definitions = parameters.Definitions;
            }

            public byte[] Run()
            {
                byte[] masks = new byte[LocationCount];

                if (_info == null || _definitions == null)
                {
                    Refuse("missing-program-metadata");
                    return masks;
                }

                ShaderStage stage = _definitions.Stage;
                if (stage is not (ShaderStage.Vertex or ShaderStage.Fragment))
                {
                    Refuse("unsupported-stage");
                    return masks;
                }

                if (_definitions.IaIndexing || _definitions.OaIndexing)
                {
                    Refuse("dynamic-stage-io");
                    return masks;
                }

                if (_info.Functions == null || _info.Functions.Count != 1 || _info.Functions[0]?.MainBlock == null)
                {
                    Refuse("single-main-function-required");
                    return masks;
                }

                StructuredFunction function = _info.Functions[0];
                if (!ScanBlock(function.MainBlock, [], 0))
                {
                    Refuse(_globalRefusal ?? "unsupported-ast");
                    return masks;
                }

                if (_globalRefusal != null)
                {
                    Refuse(_globalRefusal);
                    return masks;
                }

                for (int location = 0; location < LocationCount; location++)
                {
                    if (stage == ShaderStage.Vertex)
                    {
                        if (_unsupportedOutputLocations.Contains(location))
                        {
                            Refuse("unsupported-user-output-access");
                            continue;
                        }

                        if (_aggregateOutputLocations.Contains(location))
                        {
                            Refuse("aggregate-user-output");
                            continue;
                        }

                        if (!_producerStores.TryGetValue(location, out List<AstOperation> stores))
                        {
                            continue;
                        }

                        for (int component = 0; component < ComponentCount; component++)
                        {
                            List<AstOperation> componentStores = stores.FindAll(store => GetUserOutputComponent(store) == component);
                            if (componentStores.Count == 0)
                            {
                                continue;
                            }

                            if (TryClassifyProducer(componentStores, out string reason))
                            {
                                masks[location] |= (byte)(1 << component);
                            }
                            else
                            {
                                Refuse(reason ?? "producer-unknown");
                            }
                        }
                    }
                    else
                    {
                        if (_unsupportedInputLocations.Contains(location))
                        {
                            Refuse("unsupported-user-input-access");
                            continue;
                        }

                        if (_aggregateInputLocations.Contains(location))
                        {
                            Refuse("aggregate-user-input");
                            continue;
                        }

                        if (!_consumerLoads.TryGetValue(location, out List<AstOperation> loads))
                        {
                            continue;
                        }

                        if (!HasPerspectiveInterface(location, out string interfaceReason))
                        {
                            Refuse(interfaceReason);
                            continue;
                        }

                        for (int component = 0; component < ComponentCount; component++)
                        {
                            List<AstOperation> componentLoads = loads.FindAll(load => GetUserInputComponent(load) == component);
                            if (componentLoads.Count == 0)
                            {
                                continue;
                            }

                            if (!IsPerspectiveComponent(location, component))
                            {
                                Refuse("read-component-unused-or-non-perspective");
                                continue;
                            }

                            if (TryClassifyConsumer(componentLoads, out string reason))
                            {
                                masks[location] |= (byte)(1 << component);
                            }
                            else
                            {
                                Refuse(reason ?? "consumer-unknown");
                            }
                        }
                    }
                }

                return masks;
            }

            private bool ScanBlock(AstBlock block, List<Frame> parentPath, int depth)
            {
                if (depth > MaxAstDepth)
                {
                    _globalRefusal = "ast-depth-limit";
                    return false;
                }

                int index = 0;
                foreach (IAstNode node in block)
                {
                    List<Frame> path = Append(parentPath, new Frame(block, index));
                    if (!VisitNode(node, path, depth + 1))
                    {
                        return false;
                    }

                    index++;
                }

                return true;
            }

            private bool VisitNode(IAstNode node, List<Frame> path, int depth)
            {
                if (node == null || depth > MaxAstDepth)
                {
                    _globalRefusal = "ast-depth-limit";
                    return false;
                }

                if (_activeNodes.Contains(node))
                {
                    _globalRefusal = "ast-cycle";
                    return false;
                }

                if (++_nodeCount > MaxNodes)
                {
                    _globalRefusal = "ast-size-limit";
                    return false;
                }

                if (!_points.ContainsKey(node))
                {
                    _points.Add(node, new ExecutionPoint(path.ToArray()));
                }

                if (!_visitedNodes.Add(node))
                {
                    return true;
                }

                _activeNodes.Add(node);

                bool result;
                if (node is AstBlock block)
                {
                    if (block.Type is AstBlockType.While or AstBlockType.DoWhile)
                    {
                        _globalRefusal = "loop-control";
                        result = false;
                    }
                    else
                    {
                        if (block.Condition != null)
                        {
                            if (!AddUse(block.Condition, block, -1, UseKind.Control))
                            {
                                _activeNodes.Remove(node);
                                return false;
                            }

                            if (!VisitNode(block.Condition, path, depth + 1))
                            {
                                _activeNodes.Remove(node);
                                return false;
                            }
                        }

                        result = ScanBlock(block, path, depth + 1);
                    }
                }
                else if (node is AstAssignment assignment)
                {
                    if (assignment.Destination is not AstOperand { Type: OperandType.LocalVariable } destination)
                    {
                        _globalRefusal = "unsupported-assignment-destination";
                        result = false;
                    }
                    else
                    {
                        if (!AddUse(assignment.Source, assignment, 0, UseKind.AssignmentSource))
                        {
                            _activeNodes.Remove(node);
                            return false;
                        }

                        result = VisitNode(assignment.Source, path, depth + 1);
                    }
                }
                else if (node is AstOperation operation)
                {
                    Instruction inst = operation.Inst & Instruction.Mask;
                    if (inst == Instruction.Call)
                    {
                        _globalRefusal = "helper-call";
                        result = false;
                    }
                    else if (IsUnknownSideEffect(operation) && !IsPermittedFragmentDiscard(operation))
                    {
                        _globalRefusal = "unknown-side-effect";
                        result = false;
                    }
                    else
                    {
                        result = ScanOperation(operation, path, depth + 1);
                    }
                }
                else if (node is AstOperand)
                {
                    result = true;
                }
                else if (node is AstComment)
                {
                    result = true;
                }
                else
                {
                    _globalRefusal = "unknown-ast-node";
                    result = false;
                }

                _activeNodes.Remove(node);
                return result;
            }

            private bool ScanOperation(AstOperation operation, List<Frame> path, int depth)
            {
                if (!InspectStageIo(operation))
                {
                    return false;
                }

                for (int index = 0; index < operation.SourcesCount; index++)
                {
                    IAstNode source;
                    try
                    {
                        source = operation.GetSource(index);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        _globalRefusal = "invalid-operation-source";
                        return false;
                    }

                    if (source is AstOperand operand && operand.Type == OperandType.LocalVariable &&
                        !operand.Uses.Contains(operation))
                    {
                        // Multi-destination operation outputs are definitions, not uses.
                        // Their value semantics are outside this restricted analyzer.
                        continue;
                    }

                    if (!AddUse(source, operation, index, UseKind.OperationSource))
                    {
                        return false;
                    }

                    if (!VisitNode(source, path, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool InspectStageIo(AstOperation operation)
            {
                if (operation.Inst != Instruction.Load && operation.Inst != Instruction.Store)
                {
                    return true;
                }

                bool input = operation.StorageKind is StorageKind.Input or StorageKind.InputPerPatch;
                bool output = operation.StorageKind is StorageKind.Output or StorageKind.OutputPerPatch;
                if (!input && !output)
                {
                    return true;
                }

                if (operation.SourcesCount == 0 || !TryGetConstant(operation.GetSource(0), out int ioValue))
                {
                    _globalRefusal = "dynamic-stage-io";
                    return false;
                }

                if ((IoVariable)ioValue != IoVariable.UserDefined)
                {
                    return true;
                }

                if (operation.StorageKind is StorageKind.InputPerPatch or StorageKind.OutputPerPatch)
                {
                    _globalRefusal = "per-patch-stage-io";
                    return false;
                }

                bool relevant = _definitions.Stage == ShaderStage.Vertex
                    ? output || input
                    : input || output;
                if (!relevant)
                {
                    return true;
                }

                if (operation.SourcesCount < 2 || !TryGetConstant(operation.GetSource(1), out int location))
                {
                    _globalRefusal = "dynamic-stage-io";
                    return false;
                }

                if ((uint)location >= LocationCount)
                {
                    if (input)
                    {
                        _unsupportedInputLocations.Add(location);
                    }
                    else
                    {
                        _unsupportedOutputLocations.Add(location);
                    }
                    return true;
                }

                if (input && _definitions.Stage == ShaderStage.Fragment)
                {
                    if (operation.SourcesCount == 2)
                    {
                        _aggregateInputLocations.Add(location);
                    }
                    else if (operation.SourcesCount == 3)
                    {
                        if (!TryGetConstant(operation.GetSource(2), out int component) || (uint)component >= ComponentCount)
                        {
                            _globalRefusal = "dynamic-stage-io";
                            return false;
                        }

                        AddLocationOperation(_consumerLoads, location, operation);
                    }
                    else
                    {
                        _unsupportedInputLocations.Add(location);
                    }
                }

                if (output && _definitions.Stage == ShaderStage.Vertex)
                {
                    if (operation.SourcesCount == 3)
                    {
                        _aggregateOutputLocations.Add(location);
                    }
                    else if (operation.SourcesCount == 4)
                    {
                        if (!TryGetConstant(operation.GetSource(2), out int component) || (uint)component >= ComponentCount)
                        {
                            _globalRefusal = "dynamic-stage-io";
                            return false;
                        }

                        AddLocationOperation(_producerStores, location, operation);
                    }
                    else
                    {
                        _unsupportedOutputLocations.Add(location);
                    }
                }

                // Any other user-defined stage I/O shape is either an aggregate
                // access or outside the Vertex/Fragment subset.
                if (input && _definitions.Stage == ShaderStage.Vertex && operation.SourcesCount != 3)
                {
                    _globalRefusal = "dynamic-stage-io";
                    return false;
                }

                if (output && _definitions.Stage == ShaderStage.Fragment && operation.SourcesCount < 3)
                {
                    _unsupportedOutputLocations.Add(location);
                }

                return true;
            }

            private static bool IsUnknownSideEffect(AstOperation operation)
            {
                Instruction inst = operation.Inst & Instruction.Mask;
                return (inst == Instruction.Store && operation.StorageKind is not (StorageKind.Output or StorageKind.OutputPerPatch)) ||
                       inst.IsAtomic() ||
                       inst is Instruction.ImageStore or Instruction.ImageAtomic or
                           Instruction.Barrier or Instruction.GroupMemoryBarrier or Instruction.MemoryBarrier or
                           Instruction.Discard or Instruction.EmitVertex or Instruction.EndPrimitive or
                           Instruction.Branch or Instruction.BranchIfFalse or Instruction.BranchIfTrue or
                           Instruction.LoopBreak or Instruction.LoopContinue;
            }

            private bool IsPermittedFragmentDiscard(AstOperation operation)
            {
                return _definitions.Stage == ShaderStage.Fragment &&
                    operation.Inst == Instruction.Discard &&
                    operation.SourcesCount == 0;
            }

            private bool TryClassifyProducer(List<AstOperation> stores, out string reason)
            {
                reason = null;
                bool anyData = false;

                foreach (AstOperation store in stores)
                {
                    if (store.SourcesCount != 4 || !TryGetPoint(store, out ExecutionPoint storePoint))
                    {
                        reason = "unsupported-user-output-access";
                        return false;
                    }

                    IAstNode value = store.GetSource(3);
                    TraceResult trace = TraceProducerValue(value, storePoint, AggregateType.FP32, 0, []);
                    if (!trace.Valid)
                    {
                        reason = trace.Reason;
                        return false;
                    }

                    anyData |= trace.HasData;
                    if (trace.HasData && !trace.AllDataPathsTransformed)
                    {
                        reason = "identity-or-untransformed-carrier";
                        return false;
                    }
                }

                // A finite constant-only output is already defined numerical data.
                // Any carried data, however, must have passed through a nonidentity
                // floating or explicit numerical conversion on every reaching path.
                if (anyData && stores.Count == 0)
                {
                    reason = "producer-no-reaching-store";
                    return false;
                }

                return true;
            }

            private TraceResult TraceProducerValue(
                IAstNode node,
                ExecutionPoint usePoint,
                AggregateType expectedType,
                int depth,
                HashSet<AstOperand> activeOperands)
            {
                if (++_traceStates > MaxTraceStates || depth > MaxTraceDepth)
                {
                    return TraceResult.Refused("trace-limit");
                }

                var key = new ProducerTraceKey(node, usePoint.Frames, expectedType);
                if (_producerMemo.TryGetValue(key, out TraceResult completed))
                {
                    return completed;
                }

                TraceResult result = TraceProducerValueCore(node, usePoint, expectedType, depth, activeOperands);
                if (result.Valid)
                {
                    _producerMemo.TryAdd(key, result);
                }

                return result;
            }

            private TraceResult TraceProducerValueCore(
                IAstNode node,
                ExecutionPoint usePoint,
                AggregateType expectedType,
                int depth,
                HashSet<AstOperand> activeOperands)
            {
                if (node == null || !IsScalarType(expectedType))
                {
                    return TraceResult.Refused("unknown-producer-value");
                }

                if (node is AstOperand operand)
                {
                    if (operand.Type == OperandType.Undefined)
                    {
                        return expectedType == AggregateType.FP32 && Defaults.UndefinedName == "0"
                            ? TraceResult.Constant()
                            : TraceResult.Refused("unsupported-undefined-producer-leaf");
                    }

                    if (operand.Type == OperandType.Constant)
                    {
                        if (expectedType == AggregateType.FP32 && !float.IsFinite(BitConverter.Int32BitsToSingle(operand.Value)))
                        {
                            return TraceResult.Refused("raw-or-nonfinite-float-literal");
                        }

                        return TraceResult.Constant();
                    }

                    if (operand.Type != OperandType.LocalVariable)
                    {
                        return TraceResult.Refused("unknown-producer-leaf");
                    }

                    if (!IsScalarType(operand.VarType) || BaseType(operand.VarType) != expectedType)
                    {
                        return TraceResult.Refused("implicit-or-aggregate-type-pun");
                    }

                    if (!activeOperands.Add(operand))
                    {
                        return TraceResult.Refused("definition-cycle");
                    }

                    List<IAstNode> priorDefs = [];
                    bool hasDominating = false;
                    foreach (IAstNode def in operand.Defs)
                    {
                        if (!TryGetPoint(def, out ExecutionPoint defPoint))
                        {
                            activeOperands.Remove(operand);
                            return TraceResult.Refused("unresolved-definition");
                        }

                        if (IsBefore(defPoint, usePoint))
                        {
                            priorDefs.Add(def);
                            hasDominating |= Dominates(defPoint, usePoint);
                        }
                    }

                    if (priorDefs.Count == 0 || !hasDominating)
                    {
                        activeOperands.Remove(operand);
                        return TraceResult.Refused("uninitialized-or-nondominating-definition");
                    }

                    TraceResult joined = TraceResult.Constant();
                    foreach (IAstNode def in priorDefs)
                    {
                        if (def is not AstAssignment assignment || assignment.Destination != operand || !TryGetPoint(def, out ExecutionPoint defPoint))
                        {
                            activeOperands.Remove(operand);
                            return TraceResult.Refused("unsupported-local-definition");
                        }

                        TraceResult branch = TraceProducerValue(assignment.Source, defPoint, expectedType, depth + 1, activeOperands);
                        if (!branch.Valid)
                        {
                            activeOperands.Remove(operand);
                            return branch;
                        }

                        joined = TraceResult.Join(joined, branch);
                    }

                    activeOperands.Remove(operand);
                    return joined;
                }

                if (node is not AstOperation operation)
                {
                    return TraceResult.Refused("unknown-producer-expression");
                }

                Instruction inst = operation.Inst & Instruction.Mask;
                if (inst == Instruction.ConditionalSelect)
                {
                    return TraceProducerSelect(operation, usePoint, expectedType, depth, activeOperands);
                }

                if (operation.Inst == Instruction.Load)
                {
                    return TraceProducerLoad(operation, expectedType);
                }

                if (operation.Inst == Instruction.Copy)
                {
                    if (operation.SourcesCount != 1)
                    {
                        return TraceResult.Refused("unsupported-copy");
                    }

                    return TraceProducerValue(operation.GetSource(0), usePoint, expectedType, depth + 1, activeOperands);
                }

                if (IsFloatToIntegerConversion(inst))
                {
                    AggregateType sourceType = inst == Instruction.ConvertFP32ToS32 ? AggregateType.FP32 : AggregateType.FP32;
                    AggregateType destType = inst == Instruction.ConvertFP32ToS32 ? AggregateType.S32 : AggregateType.U32;
                    if (expectedType != destType || operation.SourcesCount != 1)
                    {
                        return TraceResult.Refused("unsupported-numerical-conversion");
                    }

                    TraceResult child = TraceProducerValue(operation.GetSource(0), usePoint, sourceType, depth + 1, activeOperands);
                    return child.Valid ? child.WithTransform() : child;
                }

                if (IsIntegerToFloatConversion(inst))
                {
                    AggregateType sourceType = inst == Instruction.ConvertS32ToFP32 ? AggregateType.S32 : AggregateType.U32;
                    if (expectedType != AggregateType.FP32 || operation.SourcesCount != 1)
                    {
                        return TraceResult.Refused("unsupported-numerical-conversion");
                    }

                    TraceResult child = TraceProducerValue(operation.GetSource(0), usePoint, sourceType, depth + 1, activeOperands);
                    return child.Valid ? child.WithTransform() : child;
                }

                if (!IsNumericFloatOperation(operation, expectedType, out int sourceCount))
                {
                    return TraceResult.Refused("unsupported-producer-operation");
                }

                if (operation.SourcesCount != sourceCount)
                {
                    return TraceResult.Refused("unsupported-producer-operation-shape");
                }

                TraceResult result = TraceResult.Constant();
                for (int index = 0; index < sourceCount; index++)
                {
                    TraceResult child = TraceProducerValue(operation.GetSource(index), usePoint, AggregateType.FP32, depth + 1, activeOperands);
                    if (!child.Valid)
                    {
                        return child;
                    }

                    result = TraceResult.Join(result, child);
                }

                if (!IsIdentityOperation(operation))
                {
                    result = result.WithTransform();
                }

                return result;
            }

            private TraceResult TraceProducerSelect(
                AstOperation operation,
                ExecutionPoint usePoint,
                AggregateType expectedType,
                int depth,
                HashSet<AstOperand> activeOperands)
            {
                if (expectedType != AggregateType.FP32 ||
                    operation.SourcesCount != 3 ||
                    (operation.Inst & Instruction.FP32) == 0 ||
                    InstructionInfo.GetDestVarType(operation.Inst) != AggregateType.FP32 ||
                    InstructionInfo.GetSrcVarType(operation.Inst, 0) != AggregateType.Bool)
                {
                    return TraceResult.Refused("unsupported-producer-select-shape");
                }

                if (!ValidateBooleanPredicate(operation.GetSource(0), usePoint, depth + 1, []))
                {
                    return TraceResult.Refused("unsupported-producer-select-predicate");
                }

                TraceResult trueArm = TraceProducerValue(
                    operation.GetSource(1), usePoint, AggregateType.FP32, depth + 1, activeOperands);
                if (!trueArm.Valid)
                {
                    return trueArm;
                }

                TraceResult falseArm = TraceProducerValue(
                    operation.GetSource(2), usePoint, AggregateType.FP32, depth + 1, activeOperands);
                if (!falseArm.Valid)
                {
                    return falseArm;
                }

                // Conditional selection preserves the arm's representation. It
                // joins the data paths but never establishes numerical provenance.
                return TraceResult.Join(trueArm, falseArm);
            }

            private bool ValidateBooleanPredicate(
                IAstNode node,
                ExecutionPoint usePoint,
                int depth,
                HashSet<AstOperand> activeOperands)
            {
                if (++_traceStates > MaxTraceStates || depth > MaxTraceDepth || node == null)
                {
                    return false;
                }

                if (node is AstOperand operand)
                {
                    if (operand.Type == OperandType.Constant)
                    {
                        return operand.Value is 0 or 1;
                    }

                    if (operand.Type != OperandType.LocalVariable || !IsBooleanScalar(operand.VarType) ||
                        !activeOperands.Add(operand))
                    {
                        return false;
                    }

                    List<IAstNode> priorDefinitions = [];
                    bool hasDominatingDefinition = false;
                    foreach (IAstNode definition in operand.Defs)
                    {
                        if (!TryGetPoint(definition, out ExecutionPoint definitionPoint))
                        {
                            activeOperands.Remove(operand);
                            return false;
                        }

                        if (IsBefore(definitionPoint, usePoint))
                        {
                            priorDefinitions.Add(definition);
                            hasDominatingDefinition |= Dominates(definitionPoint, usePoint);
                        }
                    }

                    if (priorDefinitions.Count == 0 || !hasDominatingDefinition)
                    {
                        activeOperands.Remove(operand);
                        return false;
                    }

                    foreach (IAstNode definition in priorDefinitions)
                    {
                        if (definition is not AstAssignment assignment || assignment.Destination != operand ||
                            !TryGetPoint(definition, out ExecutionPoint definitionPoint) ||
                            !ValidateBooleanPredicate(assignment.Source, definitionPoint, depth + 1, activeOperands))
                        {
                            activeOperands.Remove(operand);
                            return false;
                        }
                    }

                    activeOperands.Remove(operand);
                    return true;
                }

                if (node is not AstOperation operation)
                {
                    return false;
                }

                Instruction inst = operation.Inst & Instruction.Mask;
                if (inst == Instruction.Copy)
                {
                    return operation.SourcesCount == 1 &&
                        ValidateBooleanPredicate(operation.GetSource(0), usePoint, depth + 1, activeOperands);
                }

                if (IsBooleanComparison(inst))
                {
                    if (operation.SourcesCount != 2 || InstructionInfo.GetDestVarType(operation.Inst) != AggregateType.Bool)
                    {
                        return false;
                    }

                    AggregateType firstType = InstructionInfo.GetSrcVarType(operation.Inst, 0);
                    AggregateType secondType = InstructionInfo.GetSrcVarType(operation.Inst, 1);
                    return firstType == secondType &&
                        firstType is AggregateType.FP32 or AggregateType.S32 or AggregateType.U32 or AggregateType.Bool;
                }

                if (inst == Instruction.IsNan)
                {
                    return operation.SourcesCount == 1 &&
                        InstructionInfo.GetDestVarType(operation.Inst) == AggregateType.Bool &&
                        InstructionInfo.GetSrcVarType(operation.Inst, 0) == AggregateType.FP32;
                }

                if (inst is Instruction.LogicalAnd or Instruction.LogicalOr or Instruction.LogicalExclusiveOr or Instruction.LogicalNot)
                {
                    int sourceCount = inst == Instruction.LogicalNot ? 1 : 2;
                    if (operation.SourcesCount != sourceCount || InstructionInfo.GetDestVarType(operation.Inst) != AggregateType.Bool)
                    {
                        return false;
                    }

                    for (int index = 0; index < sourceCount; index++)
                    {
                        if (InstructionInfo.GetSrcVarType(operation.Inst, index) != AggregateType.Bool ||
                            !ValidateBooleanPredicate(operation.GetSource(index), usePoint, depth + 1, activeOperands))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                return false;
            }

            private TraceResult TraceProducerLoad(AstOperation operation, AggregateType expectedType)
            {
                if (operation.StorageKind == StorageKind.ConstantBuffer)
                {
                    if (operation.SourcesCount < 2 ||
                        !TryGetConstant(operation.GetSource(0), out int binding) ||
                        !TryGetConstant(operation.GetSource(1), out int fieldIndex) ||
                        !_parameters.Properties.ConstantBuffers.TryGetValue(binding, out BufferDefinition buffer) ||
                        (uint)fieldIndex >= (uint)buffer.Type.Fields.Length)
                    {
                        return TraceResult.Refused("unknown-constant-buffer-field");
                    }

                    StructureField field = buffer.Type.Fields[fieldIndex];
                    if ((field.Type & AggregateType.ElementTypeMask) != expectedType || !IsSupportedBufferShape(field.Type, field.ArrayLength, operation.SourcesCount - 2))
                    {
                        return TraceResult.Refused("non-numeric-or-aggregate-buffer-load");
                    }

                    // All remaining operands are selectors. They are deliberately
                    // excluded from value provenance and bounds are not inferred.
                    return TraceResult.Data(false);
                }

                if (operation.StorageKind == StorageKind.Input &&
                    operation.SourcesCount == 3 &&
                    TryGetConstant(operation.GetSource(0), out int ioValue) &&
                    (IoVariable)ioValue == IoVariable.UserDefined &&
                    TryGetConstant(operation.GetSource(1), out int location) &&
                    TryGetConstant(operation.GetSource(2), out int component) &&
                    (uint)location < LocationCount && (uint)component < ComponentCount &&
                    _definitions.Stage == ShaderStage.Vertex)
                {
                    AggregateType inputType;
                    try
                    {
                        inputType = _definitions.GetUserDefinedType(location, isOutput: false) & AggregateType.ElementTypeMask;
                    }
                    catch (Exception)
                    {
                        return TraceResult.Refused("unknown-vertex-input-type");
                    }

                    if (inputType != expectedType || expectedType is not (AggregateType.FP32 or AggregateType.S32 or AggregateType.U32))
                    {
                        return TraceResult.Refused("non-numeric-vertex-input");
                    }

                    if ((_definitions.GetAttributeType(location) & AttributeType.AnyPacked) != 0)
                    {
                        return TraceResult.Refused("packed-vertex-input");
                    }

                    return TraceResult.Data(false);
                }

                return TraceResult.Refused("unsupported-producer-load");
            }

            private static bool IsSupportedBufferShape(AggregateType type, int arrayLength, int selectorCount)
            {
                AggregateType vectorCount = type & AggregateType.ElementCountMask;
                bool array = (type & AggregateType.Array) != 0;
                int requiredSelectors = (array ? 1 : 0) + (vectorCount != AggregateType.Scalar ? 1 : 0);

                if (array && arrayLength <= 0)
                {
                    return false;
                }

                return selectorCount == requiredSelectors;
            }

            private bool TryClassifyConsumer(List<AstOperation> loads, out string reason)
            {
                reason = null;
                bool reachedColor = false;

                foreach (AstOperation load in loads)
                {
                    if (load.SourcesCount != 3 || !TryGetPoint(load, out _))
                    {
                        reason = "unsupported-user-input-access";
                        return false;
                    }

                    if ((_definitions.GetUserDefinedType(GetUserInputLocation(load), isOutput: false) & AggregateType.ElementTypeMask) != AggregateType.FP32)
                    {
                        reason = "non-fp32-fragment-input";
                        return false;
                    }

                    ConsumerTrace trace = TraceConsumerValue(load, false, 0, new HashSet<IAstNode>(ReferenceComparer<IAstNode>.Instance));
                    if (!trace.Valid)
                    {
                        reason = trace.Reason;
                        return false;
                    }

                    reachedColor |= trace.ReachedColor;
                }

                if (!reachedColor)
                {
                    reason = "no-numeric-fp32-color-output";
                    return false;
                }

                return true;
            }

            private ConsumerTrace TraceConsumerValue(IAstNode node, bool transformed, int depth, HashSet<IAstNode> active)
            {
                if (++_traceStates > MaxTraceStates || depth > MaxTraceDepth)
                {
                    return ConsumerTrace.Refused("trace-limit");
                }

                var key = new ConsumerTraceKey(node, transformed);
                if (_consumerMemo.TryGetValue(key, out ConsumerTrace completed))
                {
                    return completed;
                }

                ConsumerTrace result = TraceConsumerValueCore(node, transformed, depth, active);
                if (result.Valid)
                {
                    _consumerMemo.TryAdd(key, result);
                }

                return result;
            }

            private ConsumerTrace TraceConsumerValueCore(IAstNode node, bool transformed, int depth, HashSet<IAstNode> active)
            {
                if (!active.Add(node))
                {
                    return ConsumerTrace.Refused("consumer-use-cycle");
                }

                if (!_uses.TryGetValue(node, out List<UseSite> uses) || uses.Count == 0)
                {
                    active.Remove(node);
                    return ConsumerTrace.Refused("consumer-value-has-no-sink");
                }

                ConsumerTrace result = ConsumerTrace.Success(false);
                foreach (UseSite use in uses)
                {
                    ConsumerTrace branch = TraceConsumerUse(node, use, transformed, depth, active);
                    if (!branch.Valid)
                    {
                        active.Remove(node);
                        return branch;
                    }

                    result = ConsumerTrace.Join(result, branch);
                }

                active.Remove(node);
                return result;
            }

            private ConsumerTrace TraceConsumerUse(IAstNode node, UseSite use, bool transformed, int depth, HashSet<IAstNode> active)
            {
                if (use.Kind == UseKind.Control || use.Parent is AstBlock)
                {
                    return ConsumerTrace.Refused("consumer-unmodeled-control-use");
                }

                if (use.Parent is AstAssignment assignment)
                {
                    if (use.Kind != UseKind.AssignmentSource || assignment.Source != node ||
                        assignment.Destination is not AstOperand { Type: OperandType.LocalVariable } destination ||
                        !IsScalarType(destination.VarType) || BaseType(destination.VarType) != AggregateType.FP32)
                    {
                        return ConsumerTrace.Refused("consumer-implicit-cast-or-alias");
                    }

                    if (!HasSingleDominatingConsumerDefinition(destination))
                    {
                        return ConsumerTrace.Refused("consumer-uninitialized-or-nondominating-definition");
                    }

                    return TraceConsumerValue(destination, transformed, depth + 1, active);
                }

                if (use.Parent is not AstOperation operation || use.Kind != UseKind.OperationSource)
                {
                    return ConsumerTrace.Refused("consumer-unknown-use");
                }

                int sourceIndex = use.SourceIndex;
                if (sourceIndex < 0 || sourceIndex >= operation.SourcesCount)
                {
                    return ConsumerTrace.Refused("consumer-invalid-use-index");
                }

                Instruction inst = operation.Inst & Instruction.Mask;
                if (inst == Instruction.Store)
                {
                    if (sourceIndex != operation.SourcesCount - 1)
                    {
                        return ConsumerTrace.Refused("consumer-address-escape");
                    }

                    if (operation.StorageKind == StorageKind.Output && IsFragmentColorOutput(operation, sourceIndex))
                    {
                        if (!transformed)
                        {
                            return ConsumerTrace.Refused("consumer-color-pass-through");
                        }

                        return ConsumerTrace.Success(true);
                    }

                    return ConsumerTrace.Refused("consumer-opaque-store");
                }

                if (inst == Instruction.TextureSample)
                {
                    if (!HasExactExpectedRequest(node, AggregateType.FP32))
                    {
                        return ConsumerTrace.Refused("consumer-texture-request-not-fp32-only");
                    }

                    return ConsumerTrace.Success(false);
                }

                if (IsFloatToIntegerConversion(inst))
                {
                    if (operation.SourcesCount != 1 || sourceIndex != 0)
                    {
                        return ConsumerTrace.Refused("consumer-invalid-numerical-conversion");
                    }

                    return ConsumerTrace.Success(false);
                }

                if (IsFloatComparison(operation.Inst))
                {
                    if (sourceIndex >= operation.SourcesCount ||
                        InstructionInfo.GetSrcVarType(operation.Inst, sourceIndex) != AggregateType.FP32)
                    {
                        return ConsumerTrace.Refused("consumer-invalid-numerical-comparison");
                    }

                    return ConsumerTrace.Success(false);
                }

                if (operation.Inst == Instruction.Copy)
                {
                    if (operation.SourcesCount != 1 || sourceIndex != 0)
                    {
                        return ConsumerTrace.Refused("consumer-invalid-copy");
                    }

                    return TraceConsumerValue(operation, transformed, depth + 1, active);
                }

                if (inst == Instruction.ConditionalSelect)
                {
                    if (sourceIndex == 0 ||
                        !IsNumericFloatOperation(operation, AggregateType.FP32, out int conditionalSources) ||
                        operation.SourcesCount != conditionalSources ||
                        InstructionInfo.GetSrcVarType(operation.Inst, sourceIndex) != AggregateType.FP32)
                    {
                        return ConsumerTrace.Refused("consumer-unsupported-select");
                    }

                    return TraceConsumerValue(operation, transformed, depth + 1, active);
                }

                if (!IsNumericFloatOperation(operation, AggregateType.FP32, out int sourceCount) ||
                    operation.SourcesCount != sourceCount || sourceIndex >= sourceCount ||
                    InstructionInfo.GetSrcVarType(operation.Inst, sourceIndex) != AggregateType.FP32)
                {
                    return ConsumerTrace.Refused("consumer-unsupported-operation");
                }

                bool nextTransformed = transformed || !IsIdentityOperation(operation);
                return TraceConsumerValue(operation, nextTransformed, depth + 1, active);
            }

            private bool HasSingleDominatingConsumerDefinition(AstOperand operand)
            {
                if (operand.Defs.Count != 1 || !_uses.TryGetValue(operand, out List<UseSite> uses) || uses.Count == 0)
                {
                    return false;
                }

                foreach (IAstNode definition in operand.Defs)
                {
                    if (definition is not AstAssignment assignment || assignment.Destination != operand ||
                        !TryGetPoint(definition, out ExecutionPoint definitionPoint))
                    {
                        return false;
                    }

                    foreach (UseSite use in uses)
                    {
                        if (!TryGetPoint(use.Parent, out ExecutionPoint usePoint) || !Dominates(definitionPoint, usePoint))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                return false;
            }

            private bool IsFragmentColorOutput(AstOperation operation, int sourceIndex)
            {
                if (operation.SourcesCount < 3 || !TryGetConstant(operation.GetSource(0), out int ioValue) ||
                    (IoVariable)ioValue != IoVariable.FragmentOutputColor ||
                    !TryGetConstant(operation.GetSource(1), out int location) ||
                    (uint)location >= LocationCount || sourceIndex != operation.SourcesCount - 1)
                {
                    return false;
                }

                try
                {
                    AggregateType outputType = _definitions.GetFragmentOutputColorType(location) & AggregateType.ElementTypeMask;
                    return outputType == AggregateType.FP32;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private bool HasPerspectiveInterface(int location, out string reason)
            {
                reason = null;
                ImapPixelType[] imap = _definitions.ImapTypes;
                if (imap == null || location >= imap.Length)
                {
                    reason = "unknown-interpolation";
                    return false;
                }

                ImapPixelType pixelType = imap[location];
                PixelImap[] components = [pixelType.X, pixelType.Y, pixelType.Z, pixelType.W];
                bool any = false;
                foreach (PixelImap component in components)
                {
                    if (component == PixelImap.Unused)
                    {
                        continue;
                    }

                    any = true;
                    if (component != PixelImap.Perspective)
                    {
                        reason = "non-perspective-or-mixed-interpolation";
                        return false;
                    }
                }

                if (!any)
                {
                    reason = "unused-interpolation";
                    return false;
                }

                return true;
            }

            private bool IsPerspectiveComponent(int location, int component)
            {
                ImapPixelType pixelType = _definitions.ImapTypes[location];
                PixelImap interpolation = component switch
                {
                    0 => pixelType.X,
                    1 => pixelType.Y,
                    2 => pixelType.Z,
                    3 => pixelType.W,
                    _ => PixelImap.Unused,
                };

                return interpolation == PixelImap.Perspective;
            }

            private static bool IsFloatToIntegerConversion(Instruction instruction)
            {
                return instruction is Instruction.ConvertFP32ToS32 or Instruction.ConvertFP32ToU32;
            }

            private static bool IsIntegerToFloatConversion(Instruction instruction)
            {
                return instruction is Instruction.ConvertS32ToFP32 or Instruction.ConvertU32ToFP32;
            }

            private static bool IsFloatComparison(Instruction instruction)
            {
                Instruction baseInstruction = instruction & Instruction.Mask;
                bool comparison = baseInstruction is Instruction.CompareEqual or Instruction.CompareGreater or
                    Instruction.CompareGreaterOrEqual or Instruction.CompareLess or
                    Instruction.CompareLessOrEqual or Instruction.CompareNotEqual;
                return comparison && (instruction & Instruction.FP32) != 0;
            }

            private static bool IsBooleanComparison(Instruction instruction)
            {
                return instruction is Instruction.CompareEqual or Instruction.CompareGreater or
                    Instruction.CompareGreaterOrEqual or Instruction.CompareGreaterOrEqualU32 or
                    Instruction.CompareGreaterU32 or Instruction.CompareLess or Instruction.CompareLessOrEqual or
                    Instruction.CompareLessOrEqualU32 or Instruction.CompareLessU32 or Instruction.CompareNotEqual;
            }

            private static bool IsNumericFloatOperation(AstOperation operation, AggregateType expectedType, out int sourceCount)
            {
                sourceCount = 0;
                if (expectedType != AggregateType.FP32)
                {
                    return false;
                }

                Instruction baseInstruction = operation.Inst & Instruction.Mask;
                if (baseInstruction == Instruction.ConditionalSelect)
                {
                    if ((operation.Inst & Instruction.FP32) == 0)
                    {
                        return false;
                    }

                    sourceCount = 3;
                    return true;
                }

                if (!NumericFloatInstructions.Contains(baseInstruction))
                {
                    return false;
                }

                bool intrinsicallyFp32 = baseInstruction is Instruction.Ddx or Instruction.Ddy;
                if (!intrinsicallyFp32 && (operation.Inst & Instruction.FP32) == 0)
                {
                    return false;
                }

                sourceCount = baseInstruction switch
                {
                    Instruction.Absolute or Instruction.Ceiling or Instruction.Cosine or Instruction.Ddx or
                    Instruction.Ddy or Instruction.ExponentB2 or Instruction.Floor or Instruction.LogarithmB2 or
                    Instruction.Negate or Instruction.ReciprocalSquareRoot or Instruction.Round or Instruction.Sine or
                    Instruction.SquareRoot or Instruction.Truncate => 1,
                    Instruction.Clamp or Instruction.FusedMultiplyAdd => 3,
                    _ => 2,
                };

                return true;
            }

            private static bool IsIdentityOperation(AstOperation operation)
            {
                Instruction inst = operation.Inst & Instruction.Mask;
                if (inst is Instruction.Add or Instruction.Subtract)
                {
                    if (operation.SourcesCount != 2)
                    {
                        return false;
                    }

                    if (inst == Instruction.Subtract)
                    {
                        return IsFloatConstant(operation.GetSource(1), 0f);
                    }

                    return IsFloatConstant(operation.GetSource(0), 0f) || IsFloatConstant(operation.GetSource(1), 0f);
                }

                if (inst == Instruction.Multiply && operation.SourcesCount == 2)
                {
                    return IsFloatConstant(operation.GetSource(0), 1f) || IsFloatConstant(operation.GetSource(1), 1f);
                }

                if (inst == Instruction.Divide && operation.SourcesCount == 2)
                {
                    return IsFloatConstant(operation.GetSource(1), 1f);
                }

                if (inst == Instruction.FusedMultiplyAdd && operation.SourcesCount == 3)
                {
                    return IsFloatConstant(operation.GetSource(2), 0f) &&
                        (IsFloatConstant(operation.GetSource(0), 1f) || IsFloatConstant(operation.GetSource(1), 1f));
                }

                return false;
            }

            private static bool IsFloatConstant(IAstNode node, float value)
            {
                return node is AstOperand { Type: OperandType.Constant } operand &&
                    BitConverter.Int32BitsToSingle(operand.Value) == value;
            }

            private bool HasExactExpectedRequest(IAstNode node, AggregateType expected)
            {
                return _expectedTypes != null &&
                    _expectedTypes.TryGetValue(node, out HashSet<AggregateType> types) &&
                    types.Count == 1 &&
                    types.Contains(expected);
            }

            private static bool IsScalarType(AggregateType type)
            {
                return (type & AggregateType.Array) == 0 &&
                    (type & AggregateType.ElementCountMask) == AggregateType.Scalar &&
                    BaseType(type) is AggregateType.FP32 or AggregateType.S32 or AggregateType.U32;
            }

            private static bool IsBooleanScalar(AggregateType type)
            {
                return (type & AggregateType.Array) == 0 &&
                    (type & AggregateType.ElementCountMask) == AggregateType.Scalar &&
                    BaseType(type) == AggregateType.Bool;
            }

            private static AggregateType BaseType(AggregateType type)
            {
                return type & AggregateType.ElementTypeMask;
            }

            private static bool TryGetConstant(IAstNode node, out int value)
            {
                if (node is AstOperand { Type: OperandType.Constant } operand)
                {
                    value = operand.Value;
                    return true;
                }

                value = 0;
                return false;
            }

            private static int GetUserInputLocation(AstOperation load)
            {
                return TryGetConstant(load.GetSource(1), out int location) ? location : -1;
            }

            private static int GetUserInputComponent(AstOperation load)
            {
                return TryGetConstant(load.GetSource(2), out int component) ? component : -1;
            }

            private static int GetUserOutputComponent(AstOperation store)
            {
                return TryGetConstant(store.GetSource(2), out int component) ? component : -1;
            }

            private static void AddLocationOperation(Dictionary<int, List<AstOperation>> map, int location, AstOperation operation)
            {
                if (!map.TryGetValue(location, out List<AstOperation> operations))
                {
                    map.Add(location, operations = []);
                }

                operations.Add(operation);
            }

            private bool AddUse(IAstNode source, IAstNode parent, int index, UseKind kind)
            {
                if (source == null)
                {
                    _globalRefusal = "null-ast-source";
                    return false;
                }

                if (source is AstOperand { Type: OperandType.LocalVariable } operand && !operand.Uses.Contains(parent))
                {
                    return true;
                }

                if (_edgeCount >= MaxEdges)
                {
                    _globalRefusal = "ast-size-limit";
                    return false;
                }

                if (!_uses.TryGetValue(source, out List<UseSite> uses))
                {
                    _uses.Add(source, uses = []);
                }

                uses.Add(new UseSite(parent, index, kind));
                _edgeCount++;
                return true;
            }

            private bool TryGetPoint(IAstNode node, out ExecutionPoint point)
            {
                return _points.TryGetValue(node, out point);
            }

            private static bool IsBefore(ExecutionPoint definition, ExecutionPoint use)
            {
                int common = Math.Min(definition.Frames.Length, use.Frames.Length);
                for (int i = 0; i < common; i++)
                {
                    Frame defFrame = definition.Frames[i];
                    Frame useFrame = use.Frames[i];
                    if (!ReferenceEquals(defFrame.Block, useFrame.Block))
                    {
                        return defFrame.Index < useFrame.Index;
                    }

                    if (defFrame.Index != useFrame.Index)
                    {
                        return defFrame.Index < useFrame.Index;
                    }
                }

                return false;
            }

            private static bool Dominates(ExecutionPoint definition, ExecutionPoint use)
            {
                if (definition.Frames.Length == 0 || use.Frames.Length == 0)
                {
                    return false;
                }

                Frame defSite = definition.Frames[^1];
                for (int i = 0; i < use.Frames.Length; i++)
                {
                    Frame useFrame = use.Frames[i];
                    if (ReferenceEquals(defSite.Block, useFrame.Block))
                    {
                        return defSite.Index < useFrame.Index;
                    }
                }

                return false;
            }

            private static List<Frame> Append(List<Frame> frames, Frame frame)
            {
                List<Frame> result = new(frames.Count + 1);
                result.AddRange(frames);
                result.Add(frame);
                return result;
            }

            public void Refuse(string reason)
            {
                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = "unknown";
                }

                _refusals.TryGetValue(reason, out int count);
                _refusals[reason] = count + 1;
            }

            private readonly struct Frame
            {
                public AstBlock Block { get; }
                public int Index { get; }

                public Frame(AstBlock block, int index)
                {
                    Block = block;
                    Index = index;
                }
            }

            private readonly struct ExecutionPoint
            {
                public Frame[] Frames { get; }

                public ExecutionPoint(Frame[] frames)
                {
                    Frames = frames;
                }
            }

            private readonly struct UseSite
            {
                public IAstNode Parent { get; }
                public int SourceIndex { get; }
                public UseKind Kind { get; }

                public UseSite(IAstNode parent, int sourceIndex, UseKind kind)
                {
                    Parent = parent;
                    SourceIndex = sourceIndex;
                    Kind = kind;
                }
            }

            private enum UseKind
            {
                AssignmentSource,
                OperationSource,
                Control,
            }

            private readonly struct TraceResult
            {
                public bool Valid { get; }
                public bool HasData { get; }
                public bool AllDataPathsTransformed { get; }
                public string Reason { get; }

                private TraceResult(bool valid, bool hasData, bool allDataPathsTransformed, string reason)
                {
                    Valid = valid;
                    HasData = hasData;
                    AllDataPathsTransformed = allDataPathsTransformed;
                    Reason = reason;
                }

                public static TraceResult Constant() => new(true, false, true, null);
                public static TraceResult Data(bool transformed) => new(true, true, transformed, null);
                public static TraceResult Refused(string reason) => new(false, false, false, reason);

                public static TraceResult Join(TraceResult left, TraceResult right)
                {
                    if (!left.Valid)
                    {
                        return left;
                    }
                    if (!right.Valid)
                    {
                        return right;
                    }

                    return new TraceResult(
                        true,
                        left.HasData || right.HasData,
                        (!left.HasData || left.AllDataPathsTransformed) &&
                        (!right.HasData || right.AllDataPathsTransformed),
                        null);
                }

                public TraceResult WithTransform()
                {
                    return Valid ? new TraceResult(true, HasData, true, null) : this;
                }
            }

            private readonly struct ConsumerTrace
            {
                public bool Valid { get; }
                public bool ReachedColor { get; }
                public string Reason { get; }

                private ConsumerTrace(bool valid, bool reachedColor, string reason)
                {
                    Valid = valid;
                    ReachedColor = reachedColor;
                    Reason = reason;
                }

                public static ConsumerTrace Success(bool reachedColor) => new(true, reachedColor, null);
                public static ConsumerTrace Refused(string reason) => new(false, false, reason);

                public static ConsumerTrace Join(ConsumerTrace left, ConsumerTrace right)
                {
                    return !left.Valid ? left : !right.Valid ? right : Success(left.ReachedColor || right.ReachedColor);
                }
            }

            private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
            {
                public static readonly ReferenceComparer<T> Instance = new();
                public bool Equals(T x, T y) => ReferenceEquals(x, y);
                public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
