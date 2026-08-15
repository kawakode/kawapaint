using KawaPaint.Engine;

// Headless check of the linear gradient tool op.
using var s = new Surface(300, 200);
s.Clear(ColorBgra.White);
GradientOps.LinearGradient(s, 20, 20, 280, 180,
    ColorBgra.FromBgr(220, 40, 40),    // blue-ish
    ColorBgra.FromBgr(40, 40, 220));   // red-ish
s.Save(Path.Combine(AppContext.BaseDirectory, "gradient_test.png"));
Console.WriteLine($"start={s[20, 20]} mid={s[150, 100]} end={s[280, 180]}");
Console.WriteLine("saved gradient_test.png");
