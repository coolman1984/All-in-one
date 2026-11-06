// A stand-in for a small expert loaded on demand. It holds a little work memory so that unloading can be seen.
using System.Windows.Forms;

namespace ModuleSample;

public sealed class Module
{
    readonly byte[] work = new byte[32 * 1024 * 1024];

    public Control Build()
    {
        for (int i = 0; i < work.Length; i += 4096) work[i] = 1;
        return new Label { Text = "وحدة تحمل عند الطلب", Dock = DockStyle.Fill };
    }
}
