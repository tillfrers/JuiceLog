using System.Buffers.Binary;
using System.Text;

namespace JuiceLog.Recognition.TfLite;

public sealed class TfLiteModel
{
    private readonly Tensor[] _tensors;
    private readonly Operation[] _operations;
    private readonly int _inputIndex;
    private readonly int _outputIndex;
    
    public int InputHeight { get; }
    public int InputWidth { get; }
    public int InputChannels { get; }
    
    public int OutputLength { get; }

    public static TfLiteModel Load(string path) => new(File.ReadAllBytes(path));

    public TfLiteModel(byte[] data)
    {
        var reader = new FlatBufferReader(data);
        var model = reader.Root();

        // operator codes
        var opCodesVec = reader.VectorField(model, 1);
        var opCodes = new int[reader.VectorLength(opCodesVec)];
        for (var i = 0; i < opCodes.Length; i++)
        {
            var table = reader.VectorTable(opCodesVec, i);
            var deprecated = (sbyte)reader.Byte(table, 0, 0);
            var builtin = reader.Int32(table, 3, -1);
            opCodes[i] = builtin >= 0 ? Math.Max(builtin, deprecated) : deprecated;
        }

        var buffersVec = reader.VectorField(model, 4);
        var subGraph = reader.VectorTable(reader.VectorField(model, 2), 0);

        // tensors
        var tensorsVec = reader.VectorField(subGraph, 0);
        _tensors = new Tensor[reader.VectorLength(tensorsVec)];
        for (var i = 0; i < _tensors.Length; i++)
        {
            _tensors[i] = ReadTensor(reader, reader.VectorTable(tensorsVec, i), buffersVec);
        }

        var inputs = reader.IntVector(reader.VectorField(subGraph, 1));
        var outputs = reader.IntVector(reader.VectorField(subGraph, 2));
        if (inputs.Length != 1 || outputs.Length != 1)
        {
            throw new NotSupportedException("Only models with exactly one input and one output are supported.");
        }

        _inputIndex = inputs[0];
        _outputIndex = outputs[0];

        var inputShape = _tensors[_inputIndex].Shape;
        if (inputShape.Length != 4 || inputShape[0] != 1)
        {
            throw new NotSupportedException("Model input must have shape [1, height, width, channels].");
        }

        InputHeight = inputShape[1];
        InputWidth = inputShape[2];
        InputChannels = inputShape[3];
        OutputLength = _tensors[_outputIndex].Length;

        // operators
        var opsVec = reader.VectorField(subGraph, 3);
        _operations = new Operation[reader.VectorLength(opsVec)];
        for (var i = 0; i < _operations.Length; i++)
        {
            var table = reader.VectorTable(opsVec, i);
            var code = opCodes[reader.Int32(table, 0, 0)];
            _operations[i] = ReadOperation(reader, table, code);
        }
    }
    
    public float[] Run(ReadOnlySpan<float> input)
    {
        if (input.Length != InputHeight * InputWidth * InputChannels)
        {
            throw new ArgumentException($"Expected {InputHeight * InputWidth * InputChannels} input values, got {input.Length}.");
        }

        var buffers = new float[_tensors.Length][];
        for (var i = 0; i < _tensors.Length; i++)
        {
            buffers[i] = _tensors[i].Constant ?? new float[_tensors[i].Length];
        }

        input.CopyTo(buffers[_inputIndex]);

        foreach (var op in _operations)
        {
            Execute(op, buffers);
        }

        return buffers[_outputIndex];
    }

    // ---------------------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------------------

    private static Tensor ReadTensor(FlatBufferReader reader, int table, int buffersVec)
    {
        var shape = reader.IntVector(reader.VectorField(table, 0));
        var type = (TensorType)reader.Byte(table, 1, 0);
        var bufferIndex = reader.Int32(table, 2, 0);

        float[] scales = [];
        long[] zeroPoints = [];
        var quantizedDimension = 0;
        var quantization = reader.TableField(table, 4);
        if (quantization != 0)
        {
            scales = reader.FloatVector(reader.VectorField(quantization, 2));
            zeroPoints = reader.LongVector(reader.VectorField(quantization, 3));
            quantizedDimension = reader.Int32(quantization, 6, 0);
        }

        var buffer = bufferIndex < reader.VectorLength(buffersVec) ? reader.VectorTable(buffersVec, bufferIndex) : 0;
        var dataVec = buffer == 0 ? 0 : reader.VectorField(buffer, 0);
        var dataLength = reader.VectorLength(dataVec);

        var tensor = new Tensor(shape);
        if (dataLength == 0)
        {
            return tensor; // activation tensor, allocated at run time
        }

        var raw = reader.Bytes(dataVec + 4, dataLength);
        tensor.Constant = Dequantize(raw, type, tensor.Length, shape, scales, zeroPoints, quantizedDimension);
        return tensor;
    }

    private static float[] Dequantize(ReadOnlySpan<byte> raw, TensorType type, int length, int[] shape,
        float[] scales, long[] zeroPoints, int quantizedDimension)
    {
        var values = new float[length];
        var hasQuantization = scales.Length > 0;

        // stride of the quantized dimension, needed for per-channel quantization
        var channelStride = 1;
        for (var d = quantizedDimension + 1; d < shape.Length; d++)
        {
            channelStride *= shape[d];
        }

        var channels = shape.Length > 0 ? shape[Math.Min(quantizedDimension, shape.Length - 1)] : 1;

        for (var i = 0; i < length; i++)
        {
            float value = type switch
            {
                TensorType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(raw[(i * 4)..]),
                TensorType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(raw[(i * 4)..]),
                TensorType.Int8 => (sbyte)raw[i],
                TensorType.UInt8 => raw[i],
                TensorType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(raw[(i * 8)..]),
                _ => throw new NotSupportedException($"Tensor type {type} is not supported."),
            };

            if (hasQuantization && type != TensorType.Float32)
            {
                var channel = scales.Length > 1 ? (i / channelStride) % channels : 0;
                var zeroPoint = zeroPoints.Length > channel ? zeroPoints[channel] : 0;
                value = (value - zeroPoint) * scales[channel];
            }

            values[i] = value;
        }

        return values;
    }

    private static Operation ReadOperation(FlatBufferReader reader, int table, int code)
    {
        var inputs = reader.IntVector(reader.VectorField(table, 1));
        var outputs = reader.IntVector(reader.VectorField(table, 2));
        var options = reader.TableField(table, 4);

        var op = new Operation { Inputs = inputs, Outputs = outputs };

        switch (code)
        {
            case (int)BuiltinOperator.Quantize:
            case (int)BuiltinOperator.Dequantize:
            case (int)BuiltinOperator.Reshape:
                op.Kind = OperationKind.Copy;
                break;
            case (int)BuiltinOperator.Add:
                op.Kind = OperationKind.Add;
                op.Activation = (Activation)reader.Byte(options, 0, 0);
                break;
            case (int)BuiltinOperator.Mul:
                op.Kind = OperationKind.Mul;
                op.Activation = (Activation)reader.Byte(options, 0, 0);
                break;
            case (int)BuiltinOperator.Conv2D:
                op.Kind = OperationKind.Conv2D;
                op.Padding = (Padding)reader.Byte(options, 0, 0);
                op.StrideW = reader.Int32(options, 1, 1);
                op.StrideH = reader.Int32(options, 2, 1);
                op.Activation = (Activation)reader.Byte(options, 3, 0);
                op.DilationW = reader.Int32(options, 4, 1);
                op.DilationH = reader.Int32(options, 5, 1);
                break;
            case (int)BuiltinOperator.DepthwiseConv2D:
                op.Kind = OperationKind.DepthwiseConv2D;
                op.Padding = (Padding)reader.Byte(options, 0, 0);
                op.StrideW = reader.Int32(options, 1, 1);
                op.StrideH = reader.Int32(options, 2, 1);
                op.DepthMultiplier = reader.Int32(options, 3, 1);
                op.Activation = (Activation)reader.Byte(options, 4, 0);
                op.DilationW = reader.Int32(options, 5, 1);
                op.DilationH = reader.Int32(options, 6, 1);
                break;
            case (int)BuiltinOperator.MaxPool2D:
            case (int)BuiltinOperator.AveragePool2D:
                op.Kind = code == (int)BuiltinOperator.MaxPool2D ? OperationKind.MaxPool : OperationKind.AveragePool;
                op.Padding = (Padding)reader.Byte(options, 0, 0);
                op.StrideW = reader.Int32(options, 1, 1);
                op.StrideH = reader.Int32(options, 2, 1);
                op.FilterW = reader.Int32(options, 3, 1);
                op.FilterH = reader.Int32(options, 4, 1);
                op.Activation = (Activation)reader.Byte(options, 5, 0);
                break;
            case (int)BuiltinOperator.FullyConnected:
                op.Kind = OperationKind.FullyConnected;
                op.Activation = (Activation)reader.Byte(options, 0, 0);
                break;
            case (int)BuiltinOperator.Softmax:
                op.Kind = OperationKind.Softmax;
                op.Beta = reader.Float(options, 0, 1f);
                break;
            case (int)BuiltinOperator.Relu:
                op.Kind = OperationKind.Copy;
                op.Activation = Activation.Relu;
                break;
            case (int)BuiltinOperator.Relu6:
                op.Kind = OperationKind.Copy;
                op.Activation = Activation.Relu6;
                break;
            case (int)BuiltinOperator.LeakyRelu:
                op.Kind = OperationKind.LeakyRelu;
                op.Alpha = reader.Float(options, 0, 0.2f);
                break;
            case (int)BuiltinOperator.Logistic:
                op.Kind = OperationKind.Logistic;
                break;
            default:
                throw new NotSupportedException($"TFLite builtin operator {code} is not supported by this interpreter.");
        }

        return op;
    }

    // ---------------------------------------------------------------------------------------------
    // Execution
    // ---------------------------------------------------------------------------------------------

    private void Execute(Operation op, float[][] buffers)
    {
        var output = buffers[op.Outputs[0]];
        var outputShape = _tensors[op.Outputs[0]].Shape;

        switch (op.Kind)
        {
            case OperationKind.Copy:
                Array.Copy(buffers[op.Inputs[0]], output, output.Length);
                break;
            case OperationKind.Add:
            case OperationKind.Mul:
                ElementWise(op, buffers[op.Inputs[0]], buffers[op.Inputs[1]], output);
                break;
            case OperationKind.Conv2D:
                Conv2D(op, buffers[op.Inputs[0]], _tensors[op.Inputs[0]].Shape,
                    buffers[op.Inputs[1]], _tensors[op.Inputs[1]].Shape,
                    op.Inputs.Length > 2 && op.Inputs[2] >= 0 ? buffers[op.Inputs[2]] : null,
                    output, outputShape);
                break;
            case OperationKind.DepthwiseConv2D:
                DepthwiseConv2D(op, buffers[op.Inputs[0]], _tensors[op.Inputs[0]].Shape,
                    buffers[op.Inputs[1]], _tensors[op.Inputs[1]].Shape,
                    op.Inputs.Length > 2 && op.Inputs[2] >= 0 ? buffers[op.Inputs[2]] : null,
                    output, outputShape);
                break;
            case OperationKind.MaxPool:
            case OperationKind.AveragePool:
                Pool(op, buffers[op.Inputs[0]], _tensors[op.Inputs[0]].Shape, output, outputShape);
                break;
            case OperationKind.FullyConnected:
                FullyConnected(op, buffers[op.Inputs[0]], buffers[op.Inputs[1]], _tensors[op.Inputs[1]].Shape,
                    op.Inputs.Length > 2 && op.Inputs[2] >= 0 ? buffers[op.Inputs[2]] : null, output);
                break;
            case OperationKind.Softmax:
                Softmax(op.Beta, buffers[op.Inputs[0]], output, outputShape[^1]);
                break;
            case OperationKind.LeakyRelu:
            {
                var input = buffers[op.Inputs[0]];
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = input[i] < 0 ? input[i] * op.Alpha : input[i];
                }
                break;
            }
            case OperationKind.Logistic:
            {
                var input = buffers[op.Inputs[0]];
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = 1f / (1f + MathF.Exp(-input[i]));
                }
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown operation kind {op.Kind}.");
        }

        Activate(op.Activation, output);
    }

    private static void ElementWise(Operation op, float[] a, float[] b, float[] output)
    {
        // Make "a" the operand with the full shape and "b" the (possibly broadcast) one.
        if (b.Length > a.Length)
        {
            (a, b) = (b, a);
        }

        var channels = b.Length;
        if (channels != 1 && channels != a.Length && a.Length % channels != 0)
        {
            throw new NotSupportedException("Unsupported broadcast shapes in element-wise operation.");
        }

        for (var i = 0; i < output.Length; i++)
        {
            var bv = channels == 1 ? b[0] : channels == a.Length ? b[i] : b[i % channels];
            output[i] = op.Kind == OperationKind.Add ? a[i] + bv : a[i] * bv;
        }
    }

    private static void Conv2D(Operation op, float[] input, int[] inShape, float[] weights, int[] wShape,
        float[]? bias, float[] output, int[] outShape)
    {
        int inH = inShape[1], inW = inShape[2], inC = inShape[3];
        int outC = wShape[0], kH = wShape[1], kW = wShape[2];
        int outH = outShape[1], outW = outShape[2];

        var (padTop, padLeft) = ComputePadding(op, inH, inW, outH, outW, kH, kW);

        for (var oy = 0; oy < outH; oy++)
        {
            for (var ox = 0; ox < outW; ox++)
            {
                for (var oc = 0; oc < outC; oc++)
                {
                    var sum = bias?[oc] ?? 0f;
                    for (var ky = 0; ky < kH; ky++)
                    {
                        var iy = oy * op.StrideH - padTop + ky * op.DilationH;
                        if (iy < 0 || iy >= inH) continue;

                        for (var kx = 0; kx < kW; kx++)
                        {
                            var ix = ox * op.StrideW - padLeft + kx * op.DilationW;
                            if (ix < 0 || ix >= inW) continue;

                            var inOffset = (iy * inW + ix) * inC;
                            var wOffset = ((oc * kH + ky) * kW + kx) * inC;
                            for (var ic = 0; ic < inC; ic++)
                            {
                                sum += input[inOffset + ic] * weights[wOffset + ic];
                            }
                        }
                    }

                    output[(oy * outW + ox) * outC + oc] = sum;
                }
            }
        }
    }

    private static void DepthwiseConv2D(Operation op, float[] input, int[] inShape, float[] weights, int[] wShape,
        float[]? bias, float[] output, int[] outShape)
    {
        int inH = inShape[1], inW = inShape[2], inC = inShape[3];
        int kH = wShape[1], kW = wShape[2], outC = wShape[3];
        int outH = outShape[1], outW = outShape[2];
        var multiplier = op.DepthMultiplier <= 0 ? outC / inC : op.DepthMultiplier;

        var (padTop, padLeft) = ComputePadding(op, inH, inW, outH, outW, kH, kW);

        for (var oy = 0; oy < outH; oy++)
        {
            for (var ox = 0; ox < outW; ox++)
            {
                for (var oc = 0; oc < outC; oc++)
                {
                    var ic = oc / multiplier;
                    var sum = bias?[oc] ?? 0f;
                    for (var ky = 0; ky < kH; ky++)
                    {
                        var iy = oy * op.StrideH - padTop + ky * op.DilationH;
                        if (iy < 0 || iy >= inH) continue;

                        for (var kx = 0; kx < kW; kx++)
                        {
                            var ix = ox * op.StrideW - padLeft + kx * op.DilationW;
                            if (ix < 0 || ix >= inW) continue;

                            sum += input[(iy * inW + ix) * inC + ic] * weights[(ky * kW + kx) * outC + oc];
                        }
                    }

                    output[(oy * outW + ox) * outC + oc] = sum;
                }
            }
        }
    }

    private static void Pool(Operation op, float[] input, int[] inShape, float[] output, int[] outShape)
    {
        int inH = inShape[1], inW = inShape[2], channels = inShape[3];
        int outH = outShape[1], outW = outShape[2];
        var (padTop, padLeft) = ComputePadding(op, inH, inW, outH, outW, op.FilterH, op.FilterW);
        var isMax = op.Kind == OperationKind.MaxPool;

        for (var oy = 0; oy < outH; oy++)
        {
            for (var ox = 0; ox < outW; ox++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var acc = isMax ? float.NegativeInfinity : 0f;
                    var count = 0;
                    for (var ky = 0; ky < op.FilterH; ky++)
                    {
                        var iy = oy * op.StrideH - padTop + ky;
                        if (iy < 0 || iy >= inH) continue;

                        for (var kx = 0; kx < op.FilterW; kx++)
                        {
                            var ix = ox * op.StrideW - padLeft + kx;
                            if (ix < 0 || ix >= inW) continue;

                            var v = input[(iy * inW + ix) * channels + c];
                            acc = isMax ? MathF.Max(acc, v) : acc + v;
                            count++;
                        }
                    }

                    output[(oy * outW + ox) * channels + c] = isMax ? acc : (count > 0 ? acc / count : 0f);
                }
            }
        }
    }

    private static void FullyConnected(Operation op, float[] input, float[] weights, int[] wShape, float[]? bias, float[] output)
    {
        int outUnits = wShape[0], inUnits = wShape[1];
        if (input.Length != inUnits)
        {
            throw new InvalidOperationException($"FullyConnected expects {inUnits} inputs, got {input.Length}.");
        }

        for (var o = 0; o < outUnits; o++)
        {
            var sum = bias?[o] ?? 0f;
            var wOffset = o * inUnits;
            for (var i = 0; i < inUnits; i++)
            {
                sum += input[i] * weights[wOffset + i];
            }

            output[o] = sum;
        }
    }

    private static void Softmax(float beta, float[] input, float[] output, int lastDim)
    {
        for (var start = 0; start < input.Length; start += lastDim)
        {
            var max = float.NegativeInfinity;
            for (var i = 0; i < lastDim; i++) max = MathF.Max(max, input[start + i]);

            var sum = 0f;
            for (var i = 0; i < lastDim; i++)
            {
                output[start + i] = MathF.Exp((input[start + i] - max) * beta);
                sum += output[start + i];
            }

            for (var i = 0; i < lastDim; i++) output[start + i] /= sum;
        }
    }

    private static (int padTop, int padLeft) ComputePadding(Operation op, int inH, int inW, int outH, int outW, int kH, int kW)
    {
        if (op.Padding == Padding.Valid)
        {
            return (0, 0);
        }

        var effKH = (kH - 1) * op.DilationH + 1;
        var effKW = (kW - 1) * op.DilationW + 1;
        var padH = Math.Max((outH - 1) * op.StrideH + effKH - inH, 0);
        var padW = Math.Max((outW - 1) * op.StrideW + effKW - inW, 0);
        return (padH / 2, padW / 2);
    }

    private static void Activate(Activation activation, float[] values)
    {
        switch (activation)
        {
            case Activation.None:
                return;
            case Activation.Relu:
                for (var i = 0; i < values.Length; i++) values[i] = MathF.Max(0f, values[i]);
                return;
            case Activation.ReluN1To1:
                for (var i = 0; i < values.Length; i++) values[i] = Math.Clamp(values[i], -1f, 1f);
                return;
            case Activation.Relu6:
                for (var i = 0; i < values.Length; i++) values[i] = Math.Clamp(values[i], 0f, 6f);
                return;
            case Activation.Tanh:
                for (var i = 0; i < values.Length; i++) values[i] = MathF.Tanh(values[i]);
                return;
            default:
                throw new NotSupportedException($"Fused activation {activation} is not supported.");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Types
    // ---------------------------------------------------------------------------------------------

    private sealed class Tensor(int[] shape)
    {
        public int[] Shape { get; } = shape;
        public int Length { get; } = shape.Aggregate(1, (a, b) => a * b);
        public float[]? Constant { get; set; }
    }

    private sealed class Operation
    {
        public OperationKind Kind { get; set; }
        public int[] Inputs { get; set; } = [];
        public int[] Outputs { get; set; } = [];
        public Padding Padding { get; set; }
        public int StrideW { get; set; } = 1;
        public int StrideH { get; set; } = 1;
        public int DilationW { get; set; } = 1;
        public int DilationH { get; set; } = 1;
        public int FilterW { get; set; } = 1;
        public int FilterH { get; set; } = 1;
        public int DepthMultiplier { get; set; } = 1;
        public Activation Activation { get; set; }
        public float Alpha { get; set; }
        public float Beta { get; set; } = 1f;
    }

    private enum OperationKind
    {
        Copy,
        Add,
        Mul,
        Conv2D,
        DepthwiseConv2D,
        MaxPool,
        AveragePool,
        FullyConnected,
        Softmax,
        LeakyRelu,
        Logistic,
    }

    private enum TensorType : byte
    {
        Float32 = 0,
        Float16 = 1,
        Int32 = 2,
        UInt8 = 3,
        Int64 = 4,
        String = 5,
        Bool = 6,
        Int16 = 7,
        Complex64 = 8,
        Int8 = 9,
    }

    private enum BuiltinOperator
    {
        Add = 0,
        AveragePool2D = 1,
        Conv2D = 3,
        DepthwiseConv2D = 4,
        Dequantize = 6,
        FullyConnected = 9,
        Logistic = 14,
        MaxPool2D = 17,
        Mul = 18,
        Relu = 19,
        Relu6 = 21,
        Reshape = 22,
        Softmax = 25,
        LeakyRelu = 98,
        Quantize = 114,
    }

    private enum Padding : byte
    {
        Same = 0,
        Valid = 1,
    }

    private enum Activation : byte
    {
        None = 0,
        Relu = 1,
        ReluN1To1 = 2,
        Relu6 = 3,
        Tanh = 4,
        SignBit = 5,
    }
    
    private sealed class FlatBufferReader(byte[] data)
    {
        public int Root() => Indirect(0);

        private int U32(int pos) => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        private int I32(int pos) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
        private int U16(int pos) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        private int Indirect(int pos) => pos + U32(pos);
        
        private int FieldOffset(int table, int fieldId)
        {
            if (table == 0) return 0;
            var vtable = table - I32(table);
            var vtableSize = U16(vtable);
            var entry = 4 + 2 * fieldId;
            return entry < vtableSize ? U16(vtable + entry) : 0;
        }

        public int Int32(int table, int fieldId, int defaultValue)
        {
            var offset = FieldOffset(table, fieldId);
            return offset == 0 ? defaultValue : I32(table + offset);
        }

        public byte Byte(int table, int fieldId, byte defaultValue)
        {
            var offset = FieldOffset(table, fieldId);
            return offset == 0 ? defaultValue : data[table + offset];
        }

        public float Float(int table, int fieldId, float defaultValue)
        {
            var offset = FieldOffset(table, fieldId);
            return offset == 0 ? defaultValue : BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(table + offset));
        }
        
        public int TableField(int table, int fieldId)
        {
            var offset = FieldOffset(table, fieldId);
            return offset == 0 ? 0 : Indirect(table + offset);
        }

        public int VectorField(int table, int fieldId) => TableField(table, fieldId);

        public int VectorLength(int vector) => vector == 0 ? 0 : U32(vector);

        public int VectorTable(int vector, int index) => Indirect(vector + 4 + 4 * index);

        public int[] IntVector(int vector)
        {
            var result = new int[VectorLength(vector)];
            for (var i = 0; i < result.Length; i++) result[i] = I32(vector + 4 + 4 * i);
            return result;
        }

        public float[] FloatVector(int vector)
        {
            var result = new float[VectorLength(vector)];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(vector + 4 + 4 * i));
            }

            return result;
        }

        public long[] LongVector(int vector)
        {
            var result = new long[VectorLength(vector)];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(vector + 4 + 8 * i));
            }

            return result;
        }

        public ReadOnlySpan<byte> Bytes(int pos, int length) => data.AsSpan(pos, length);

        public string String(int table, int fieldId)
        {
            var pos = TableField(table, fieldId);
            return pos == 0 ? string.Empty : Encoding.UTF8.GetString(data, pos + 4, U32(pos));
        }
    }
}
