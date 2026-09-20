using System.Text;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class PortableInstallRecordTests
{
    [Fact]
    public void WritePortableInstallRecord_UsesUtf8WithoutBom()
    {
        string directory = TestWorkspace.CreateDirectory();
        string record = Path.Combine(directory, "portable-install.txt");
        const string installPath = @"F:\软件 安装\CETUS 🐋";

        App.WritePortableInstallRecord(record, installPath);

        byte[] bytes = File.ReadAllBytes(record);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(installPath, Encoding.UTF8.GetString(bytes));
    }
}
