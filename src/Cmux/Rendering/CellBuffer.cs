using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Cmux.Rendering;

/// <summary>
/// Represents a single terminal cell's GPU data. Must be exactly 64 bytes and match the HLSL struct layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct CellData
{
    public Vector4 Foreground;  // 16 bytes — R,G,B,A normalized
    public Vector4 Background;  // 16 bytes
    public Vector4 AtlasUV;    // 16 bytes (u0, v0, u1, v1)
    public uint Flags;          // 4 bytes
    public uint CursorStyle;    // 4 bytes
    public uint _pad0;          // 4 bytes
    public uint _pad1;          // 4 bytes

    // Flag constants — must match HLSL definitions
    public const uint FLAG_CURSOR            = 0x001;
    public const uint FLAG_SELECTED          = 0x002;
    public const uint FLAG_SEARCH_MATCH      = 0x004;
    public const uint FLAG_CURRENT_MATCH     = 0x008;
    public const uint FLAG_UNDERLINE         = 0x010;
    public const uint FLAG_STRIKETHROUGH     = 0x020;
    public const uint FLAG_DIM               = 0x040;
    public const uint FLAG_URL_HOVER         = 0x080;
    public const uint FLAG_WIDE              = 0x100;
    public const uint FLAG_WIDE_PLACEHOLDER  = 0x200;
}

/// <summary>
/// Manages a CPU-side array of <see cref="CellData"/> elements and uploads it to the GPU
/// as a structured StructuredBuffer bound at register t0.
/// </summary>
internal sealed class CellBuffer : IDisposable
{
    private const int CellSizeBytes = 64;

    private readonly ID3D11Device _device;
    private ID3D11Buffer _gpuBuffer;
    private ID3D11ShaderResourceView _srv;
    private CellData[] _cpuData;
    private int _cols;
    private int _rows;
    private bool _disposed;

    public ID3D11ShaderResourceView SRV => _srv;
    public int CellCount => _cols * _rows;

    public CellBuffer(ID3D11Device device, int cols, int rows)
    {
        _device = device;
        _cols = cols;
        _rows = rows;
        _cpuData = new CellData[cols * rows];
        (_gpuBuffer, _srv) = CreateGpuResources(cols * rows);
    }

    /// <summary>
    /// Recreates the GPU buffer and SRV if the grid dimensions have changed.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        if (cols == _cols && rows == _rows)
            return;

        _srv.Dispose();
        _gpuBuffer.Dispose();

        _cols = cols;
        _rows = rows;
        _cpuData = new CellData[cols * rows];
        (_gpuBuffer, _srv) = CreateGpuResources(cols * rows);
    }

    /// <summary>
    /// Zeroes all cells in the CPU-side staging array, preventing stale data artifacts.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_cpuData);
    }

    /// <summary>
    /// Writes a cell into the CPU-side staging array.
    /// </summary>
    public void SetCell(int row, int col, in CellData data)
    {
        _cpuData[row * _cols + col] = data;
    }

    /// <summary>
    /// Uploads the entire CPU array to the GPU buffer via Map/WriteDiscard.
    /// </summary>
    public unsafe void Upload(ID3D11DeviceContext context)
    {
        MappedSubresource mapped = context.Map(_gpuBuffer, MapMode.WriteDiscard);
        try
        {
            ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(_cpuData.AsSpan());
            Span<byte> dst = new Span<byte>((void*)mapped.DataPointer, src.Length);
            src.CopyTo(dst);
        }
        finally
        {
            context.Unmap(_gpuBuffer, 0);
        }
    }

    private (ID3D11Buffer buffer, ID3D11ShaderResourceView srv) CreateGpuResources(int cellCount)
    {
        uint byteWidth = (uint)(cellCount * CellSizeBytes);

        ID3D11Buffer buffer = _device.CreateBuffer(
            byteWidth,
            BindFlags.ShaderResource,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.BufferStructured,
            (uint)CellSizeBytes);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = 0,
                NumElements = (uint)cellCount
            }
        };

        ID3D11ShaderResourceView srv = _device.CreateShaderResourceView(buffer, srvDesc);

        return (buffer, srv);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _srv.Dispose();
        _gpuBuffer.Dispose();
    }
}
