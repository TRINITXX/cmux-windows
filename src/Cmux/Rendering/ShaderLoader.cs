using System.Reflection;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Cmux.Rendering;

/// <summary>
/// Loads and compiles HLSL shaders from embedded resources at runtime.
/// </summary>
internal static class ShaderLoader
{
    public static ID3D11VertexShader CreateVertexShader(ID3D11Device device, out Blob bytecode)
    {
        bytecode = CompileShader("VSMain", "vs_5_0");
        return device.CreateVertexShader(bytecode);
    }

    public static ID3D11PixelShader CreatePixelShader(ID3D11Device device)
    {
        using var bytecode = CompileShader("PSMain", "ps_5_0");
        return device.CreatePixelShader(bytecode);
    }

    private static Blob CompileShader(string entryPoint, string profile)
    {
        var hlslSource = LoadShaderSource();

        Result hr = Compiler.Compile(
            hlslSource,
            entryPoint,
            "TerminalShader.hlsl",
            profile,
            out Blob? bytecode,
            out Blob? errorBlob);

        if (hr.Failure)
        {
            string errorMsg = "Unknown shader compilation error";
            if (errorBlob != null)
            {
                try { errorMsg = errorBlob.AsString() ?? errorMsg; }
                finally { errorBlob.Dispose(); }
            }
            bytecode?.Dispose();
            throw new InvalidOperationException($"HLSL compilation failed ({entryPoint}): {errorMsg}");
        }

        errorBlob?.Dispose();
        return bytecode!;
    }

    private static string LoadShaderSource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("TerminalShader.hlsl", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new InvalidOperationException("TerminalShader.hlsl embedded resource not found");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}
