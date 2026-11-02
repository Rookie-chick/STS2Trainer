// ============================================================================
//  STS2Trainer（界面版）—— 复刻 Plus20 Trainer 的小窗形态
//
//  特点：
//    · 无边框小窗（自绘标题栏 + 圆角 + 拖动移动 + 右上关闭），复刻原版视觉
//    · 功能列表：勾选框 + 名称 + 热键，实时显示"已生效 / 未就绪"
//    · 全局热键（GetAsyncKeyState 轮询），勾选与热键双向同步
//    · 自动找 SlayTheSpire2.exe 进程；找不到给提示与"重试"
//    · 关闭窗口 = 还原所有内存补丁
//
//  复用：Game.cs（ClrMD 侦查）+ Hook.cs（注入），与控制台版同一份源码
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrainerForm());
    }
}

class Feat
{
    public string Name;
    public string Key;
    public int Vk;
    public bool Ctrl;
    public int Flag;
    public bool On;
    public ulong Addr;
    public byte[] Orig;
    public string Note = "";
    public ListViewItem Item;
    public bool Ready => Orig != null;
}

class TrainerForm : Form
{
    // ---- 目标值 ----
    const int HP = 9999, SHIELD = 9999, GOLD = 9999999, STAR = 99;
    const int GOLD_REWARD = 2000, POTION_SLOTS = 20, SCORE = 999999, TREASURE = 3;
    const decimal MAX_ENERGY = 10m, UPGRADE_ODDS = 1m, MERCHANT_FREE = 0m;
    const float RARITY = 999f;
    const double TIME_SCALE = 2.0;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    const int VK_CONTROL = 0x11;

    // ---- 状态 ----
    Game g;
    IntPtr h;
    ulong flags;
    int pid;
    List<Feat> feats = new();
    ListView list;
    Label lblStatus, lblTitle;
    System.Windows.Forms.Timer timer;
    bool[] wasDown;
    long lastRetry;
    bool dmgInstalled;
    readonly List<(ulong addr, byte[] orig)> extraPatches = new();
    Point dragOffset;
    bool dragging;

    public TrainerForm()
    {
        Text = "STS2 Trainer";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(430, 600);
        BackColor = Color.FromArgb(24, 26, 32);
        DoubleBuffered = true;
        Padding = new Padding(1);

        BuildUi();
        feats = BuildFeats();
        FillList();          // ★ 先列出全部功能（未连接时显示"待机"），连上后再变"✓"
        TryConnect();

        timer = new System.Windows.Forms.Timer { Interval = 60 };
        timer.Tick += OnTick;
        timer.Start();
    }

    // ---------------------------------------------------------------- 界面
    void BuildUi()
    {
        // 标题栏
        var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(32, 35, 44) };
        bar.MouseDown += (s, e) => { dragging = true; dragOffset = e.Location; };
        bar.MouseMove += (s, e) => { if (dragging) Location = new Point(Location.X + e.X - dragOffset.X, Location.Y + e.Y - dragOffset.Y); };
        bar.MouseUp += (s, e) => dragging = false;

        lblTitle = new Label
        {
            Text = "SLAY THE SPIRE 2  ·  TRAINER",
            ForeColor = Color.FromArgb(235, 238, 245),
            Font = new Font("Segoe UI Semibold", 10.5f),
            AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0)
        };
        lblTitle.MouseDown += bar_MouseDown; lblTitle.MouseMove += bar_MouseMove; lblTitle.MouseUp += (s, e) => dragging = false;

        var close = new Button
        {
            Text = "✕", Dock = DockStyle.Right, Width = 44, FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(200, 205, 215), BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10f), TabStop = false
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (s, e) => Close();
        close.MouseEnter += (s, e) => close.BackColor = Color.FromArgb(196, 60, 60);
        close.MouseLeave += (s, e) => close.BackColor = Color.Transparent;

        bar.Controls.Add(lblTitle);
        bar.Controls.Add(close);
        Controls.Add(bar);

        // 功能列表
        list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(28, 31, 38),
            ForeColor = Color.FromArgb(226, 230, 238),
            Font = new Font("Segoe UI", 9.75f),
            OwnerDraw = false
        };
        list.Columns.Add("功能", 232);
        list.Columns.Add("热键", 92);
        list.Columns.Add("状态", 78);
        list.ItemCheck += (s, e) =>
        {
            if (e.Index < 0 || e.Index >= feats.Count) return;
            var f = feats[e.Index];
            if (!f.Ready) { e.NewValue = CheckState.Unchecked; return; }
            // 真实写入放到 ItemChecked（ItemCheck 里改状态会抛）
        };
        list.ItemChecked += (s, e) =>
        {
            int idx = e.Item.Index;
            if (idx < 0 || idx >= feats.Count) return;
            var f = feats[idx];
            if (!f.Ready) { f.On = false; return; }
            f.On = e.Item.Checked;
            Hook.WriteI32(h, FlagAddr(f.Flag), f.On ? 1 : 0);
            UpdateRow(f);
        };
        Controls.Add(list);

        // 底栏
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(32, 35, 44) };
        lblStatus = new Label
        {
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 156, 168),
            Font = new Font("Consolas", 8.75f), TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 8, 0), Text = "正在查找游戏进程…"
        };
        var btnReload = new Button
        {
            Text = "重新连接", Dock = DockStyle.Right, Width = 96, FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(220, 226, 236), BackColor = Color.FromArgb(45, 49, 60),
            Font = new Font("Segoe UI", 9f)
        };
        btnReload.FlatAppearance.BorderSize = 0;
        btnReload.Click += (s, e) => TryConnect();
        bottom.Controls.Add(lblStatus);
        bottom.Controls.Add(btnReload);
        Controls.Add(bottom);

        // 停靠顺序修正：Fill 的控件要排到最前（z-order 0），最后才被分配剩余空间
        list.BringToFront();
    }

    void bar_MouseDown(object s, MouseEventArgs e) { dragging = true; dragOffset = e.Location; }
    void bar_MouseMove(object s, MouseEventArgs e)
    {
        if (dragging) Location = new Point(Location.X + e.X - dragOffset.X, Location.Y + e.Y - dragOffset.Y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var pen = new Pen(Color.FromArgb(70, 76, 92), 1);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(r, 10);
        e.Graphics.DrawPath(pen, path);
    }

    static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    ulong FlagAddr(int i) => flags + (ulong)(i * 4);

    // ---------------------------------------------------------------- 连接
    void TryConnect()
    {
        var proc = Array.Find(Process.GetProcessesByName("SlayTheSpire2"), p => p.MainWindowHandle != IntPtr.Zero)
                   ?? Array.Find(Process.GetProcessesByName("SlayTheSpire2"), p => true);
        if (proc == null)
        {
            SetStatus("✗ 没找到 SlayTheSpire2.exe —— 先启动游戏（需 --rendering-driver opengl3），再点「重新连接」");
            return;
        }
        pid = proc.Id;

        try
        {
            g = new Game();
            g.Attach(pid);
            if (!g.Resolve()) { SetStatus("✗ 已连上 pid=" + pid + "，但没找到玩家：先进战斗再点「重新连接」"); return; }

            h = Hook.Open(pid);
            if (h == IntPtr.Zero) { SetStatus("✗ OpenProcess 失败：请以管理员身份运行"); return; }

            flags = Hook.AllocFlags(h, 24);
            Hook.WriteI32(h, g.Creature + Game.OFF_MAXHP, HP);

            InstallAll();
            RefreshRows();
            SetStatus("✓ 已连接 pid=" + pid + "   HP=" + g.ReadI32(g.Creature + Game.OFF_HP) + "/" + g.ReadI32(g.Creature + Game.OFF_MAXHP)
                      + "   金=" + g.ReadI32(g.Player + Game.OFF_PLAYER_GOLD)
                      + "   能量=" + g.ReadI32(g.Pcs + Game.OFF_PCS_ENERGY) + "   星=" + g.ReadI32(g.Pcs + Game.OFF_PCS_STARS));
        }
        catch (Exception ex)
        {
            SetStatus("✗ 连接失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    void SetStatus(string s) { if (lblStatus != null) lblStatus.Text = s; }

    // ---------------------------------------------------------------- 功能表
    List<Feat> BuildFeats() => new()
    {
        new Feat{ Name="无限血",          Key="Num 1",      Vk=0x61, Flag=0  },
        new Feat{ Name="金币锁 9999999",  Key="Ctrl+Num 1", Vk=0x61, Ctrl=true, Flag=1 },
        new Feat{ Name="无限护盾",        Key="Num 2",      Vk=0x62, Flag=2  },
        new Feat{ Name="无限能量",        Key="Num 3",      Vk=0x63, Flag=3  },
        new Feat{ Name="无限星星",        Key="Num 4",      Vk=0x64, Flag=4  },
        new Feat{ Name="奖励必给药水",    Key="Num 5",      Vk=0x65, Flag=5  },
        new Feat{ Name="奖励卡必升级",    Key="Num 6",      Vk=0x66, Flag=6  },
        new Feat{ Name="奖励最高稀有度",  Key="Num 7",      Vk=0x67, Flag=7  },
        new Feat{ Name="未知点必宝箱",    Key="Num 8",      Vk=0x68, Flag=8  },
        new Feat{ Name="敌人不额外行动",  Key="Num 9",      Vk=0x69, Flag=9  },
        new Feat{ Name="超级伤害 9999",   Key="Num 0",      Vk=0x60, Flag=10 },
        new Feat{ Name="伤害倍率 ×10",    Key="Num .",      Vk=0x6E, Flag=11 },
        new Feat{ Name="防御倍率 ÷4",     Key="Num +",      Vk=0x6B, Flag=12 },
        new Feat{ Name="金币奖励 ×2000",  Key="Ctrl+Num 2", Vk=0x62, Ctrl=true, Flag=13 },
        new Feat{ Name="商店免费",        Key="Ctrl+Num 3", Vk=0x63, Ctrl=true, Flag=14 },
        new Feat{ Name="最大能量 10",     Key="Ctrl+Num 4", Vk=0x64, Ctrl=true, Flag=15 },
        new Feat{ Name="药水槽 20",       Key="Ctrl+Num 5", Vk=0x65, Ctrl=true, Flag=16 },
        new Feat{ Name="最高分数",        Key="Ctrl+Num 6", Vk=0x66, Ctrl=true, Flag=17 },
        new Feat{ Name="游戏速度 2×",     Key="Ctrl+Num 8", Vk=0x68, Ctrl=true, Flag=18 },
    };

    void FillList()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var f in feats)
        {
            var it = new ListViewItem(new[] { f.Name, f.Key, f.Ready ? "✓" : "待机" }) { Checked = f.On };
            f.Item = it;
            list.Items.Add(it);
            Paint(it, f);
        }
        list.EndUpdate();
    }

    void RefreshRows()
    {
        foreach (var f in feats) if (f.Item != null) Paint(f.Item, f);
    }

    void Paint(ListViewItem it, Feat f)
    {
        it.SubItems[2].Text = f.Ready ? "✓" : (string.IsNullOrEmpty(f.Note) ? "待机" : f.Note);
        it.ForeColor = f.Ready ? Color.FromArgb(226, 230, 238) : Color.FromArgb(112, 118, 130);
    }

    void UpdateRow(Feat f)
    {
        if (f.Item == null) return;
        f.Item.Checked = f.On;
        Paint(f.Item, f);
    }

    // ---------------------------------------------------------------- 安装
    void InstallAll()
    {
        void Set(string name, ulong addr, byte[] orig, string why = null)
        {
            var f = feats.Find(x => x.Name == name);
            if (f == null) return;
            if (orig == null) { f.Note = why ?? (addr >= 0x10000 ? "安装失败" : "未JIT"); return; }
            f.Addr = addr; f.Orig = orig;
        }

        // A. 无限血：用原版 Plus20 的做法 —— 直接挂 Creature.set_CurrentHp（实测有机器码）
        Set("无限血", g.FnSetCurrentHp,
            g.FnSetCurrentHp >= 0x10000 ? Hook.InstallSetterHook(h, g.FnSetCurrentHp, g.Creature, HP, FlagAddr(0)) : null);

        // B. 金币/能量/星星：ClrMD 枚举不到 set_Gold / LoseEnergy / LoseStars，
        //    所以用"帧钩子 + HUD 刷新"（这条路线在 10/12/13 篇已实测通过）。
        //    ★ 关键：逐个校验，坏的那一条只跳过它自己，不再拖垮整组。
        var acts = new List<Hook.Action>();
        var actOwner = new Dictionary<string, string>();   // 动作名 → 功能名
        void TryAdd(string feat, Hook.Action a)
        {
            // 只写值的动作：CheckAddr + WriteAddr 有效即可（CallFn 可为 0）
            bool ok = a.CheckAddr >= 0x10000 && (a.WriteAddr != 0 || (a.CallFn >= 0x10000 && a.CallThis >= 0x10000));
            if (!ok)
            {
                var ff = feats.Find(x => x.Name == feat);
                if (ff != null) ff.Note = "缺地址";
                return;
            }
            acts.Add(a); actOwner[a.Name] = feat;
        }

        // 无限血：setter 还没 JIT 时用帧动作兜底（两条都开也无害，都是把血写成 9999）
        TryAdd("无限血", new Hook.Action{ Name="无限血(帧)", FlagAddr=FlagAddr(0), CheckAddr=g.Creature+Game.OFF_HP, Target=HP,
                                  WriteAddr=g.Creature+Game.OFF_HP,
                                  CallThis=g.HpBar, CallFn=g.FnUpdateHealth });
        TryAdd("金币锁 9999999", new Hook.Action{ Name="金币锁", FlagAddr=FlagAddr(1), CheckAddr=g.Player+Game.OFF_PLAYER_GOLD, Target=GOLD,
                                  WriteAddr=g.Player+Game.OFF_PLAYER_GOLD,
                                  PreAddr=g.GoldBar+Game.OFF_GOLDBAR_CURGOLD, PreValue=GOLD-1,
                                  CallThis=g.GoldBar, CallFn=g.FnUpdateGold });
        // 能量 / 星星：刷新方法枚举不到 ⇒ 只锁值（星星数字本来每帧自己刷；能量够打牌即可）
        TryAdd("无限能量", new Hook.Action{ Name="无限能量", FlagAddr=FlagAddr(3), CheckAddr=g.Pcs+Game.OFF_PCS_ENERGY,
                                  Target=Math.Max(4, g.ReadI32(g.Pcs+Game.OFF_PCS_ENERGY)),
                                  WriteAddr=g.Pcs+Game.OFF_PCS_ENERGY,
                                  CallThis=g.EnergyUi, CallFn=g.FnRefreshLabel });
        TryAdd("无限星星", new Hook.Action{ Name="无限星星", FlagAddr=FlagAddr(4), CheckAddr=g.Pcs+Game.OFF_PCS_STARS, Target=STAR,
                                  WriteAddr=g.Pcs+Game.OFF_PCS_STARS,
                                  CallThis=g.StarUi, CallFn=g.FnStarVisible });
        // 游戏速度：只有一个动作（拿不到就不装）
        if (g.FnTimeScale >= 0x10000)
            acts.Add(new Hook.Action{ Name="游戏速度", FlagAddr=FlagAddr(18), CallFnXmm0=g.FnTimeScale,
                                      Xmm0Bits=(ulong)BitConverter.DoubleToInt64Bits(TIME_SCALE) });

        if (acts.Count > 0 && g.FnProcess >= 0x10000)
        {
            var orig = Hook.InstallFrameHook(h, g.FnProcess, acts, out _);
            if (orig != null)
                foreach (var kv in actOwner)
                {
                    var f = feats.Find(x => x.Name == kv.Value);
                    if (f != null) { f.Addr = g.FnProcess; f.Orig = orig; f.Note = ""; }
                }
            else
                foreach (var kv in actOwner)
                {
                    var f = feats.Find(x => x.Name == kv.Value);
                    if (f != null) f.Note = "挂点被占用";
                }
            var sp = feats.Find(x => x.Name == "游戏速度 2×");
            if (sp != null)
            {
                if (orig != null) { sp.Addr = g.FnProcess; sp.Orig = orig; sp.Note = ""; }
                else sp.Note = "挂点被占用";
            }
        }

        // B. 护盾
        Set("无限护盾", g.FnSetBlock, g.FnSetBlock >= 0x10000 ? Hook.InstallSetterHook(h, g.FnSetBlock, g.Creature, SHIELD, FlagAddr(2)) : null);

        // C. 奖励与行为
        Set("奖励必给药水", g.FnPotionRoll, g.FnPotionRoll >= 0x10000 ? Hook.InstallForceReturnHook(h, g.FnPotionRoll, FlagAddr(5), 1) : null);
        Set("奖励卡必升级", g.FnUpgradeOdds, g.FnUpgradeOdds >= 0x10000 ? Hook.InstallForceDecimalReturnHook(h, g.FnUpgradeOdds, FlagAddr(6), UPGRADE_ODDS) : null);
        Set("奖励最高稀有度", g.FnRarityOdds, g.FnRarityOdds >= 0x10000 ? Hook.InstallForceFloatReturnHook(h, g.FnRarityOdds, FlagAddr(7), RARITY) : null);
        Set("未知点必宝箱", g.FnUnknownRoll, g.FnUnknownRoll >= 0x10000 ? Hook.InstallForceReturnHook(h, g.FnUnknownRoll, FlagAddr(8), TREASURE) : null);
        Set("敌人不额外行动", g.FnExtraTurn, g.FnExtraTurn >= 0x10000 ? Hook.InstallForceReturnHook(h, g.FnExtraTurn, FlagAddr(9), 0) : null);

        // D. 伤害三合一：★ 按原版挂 Hook::ModifyDamage（两个重载）
        if (g.FnModifyDamage1 >= 0x10000)
        {
            var d1 = Hook.InstallModifyDamageHook(h, g.FnModifyDamage1, g.Creature, FlagAddr(10), FlagAddr(11), FlagAddr(12));
            if (d1 != null) extraPatches.Add((g.FnModifyDamage1, d1));
            if (g.FnModifyDamage2 >= 0x10000)
            {
                var d2 = Hook.InstallModifyDamageHook(h, g.FnModifyDamage2, g.Creature, FlagAddr(10), FlagAddr(11), FlagAddr(12));
                if (d2 != null) extraPatches.Add((g.FnModifyDamage2, d2));
            }
            bool ok = d1 != null;
            foreach (var n in new[] { "超级伤害 9999", "伤害倍率 ×10", "防御倍率 ÷4" })
            {
                var f = feats.Find(x => x.Name == n);
                if (f == null) continue;
                if (ok) { f.Addr = g.FnModifyDamage1; f.Orig = d1; f.Note = ""; }
                else f.Note = "安装失败";
            }
            if (ok) dmgInstalled = true;
        }
        else
        {
            foreach (var n in new[] { "超级伤害 9999", "伤害倍率 ×10", "防御倍率 ÷4" })
            { var f = feats.Find(x => x.Name == n); if (f != null) f.Note = "未JIT"; }
        }

        // E. 经济与上限
        Set("金币奖励 ×2000", g.FnMinGold, g.FnMinGold >= 0x10000 ? Hook.InstallForceReturnHook(h, g.FnMinGold, FlagAddr(13), GOLD_REWARD) : null);
        if (g.FnMaxGold >= 0x10000) Hook.InstallForceReturnHook(h, g.FnMaxGold, FlagAddr(13), GOLD_REWARD);
        Set("商店免费", g.FnMerchantPrice, g.FnMerchantPrice >= 0x10000 ? Hook.InstallForceDecimalReturnHook(h, g.FnMerchantPrice, FlagAddr(14), MERCHANT_FREE) : null);
        Set("最大能量 10", g.FnMaxEnergy, g.FnMaxEnergy >= 0x10000 ? Hook.InstallForceDecimalReturnHook(h, g.FnMaxEnergy, FlagAddr(15), MAX_ENERGY) : null);
        Set("药水槽 20", g.FnSetPotionSlots, g.FnSetPotionSlots >= 0x10000 ? Hook.InstallSetterHook(h, g.FnSetPotionSlots, 0, POTION_SLOTS, FlagAddr(16)) : null);

        // F. 分数（两个重载）
        if (g.FnScore1 >= 0x10000) Set("最高分数", g.FnScore1, Hook.InstallForceReturnHook(h, g.FnScore1, FlagAddr(17), SCORE));
        if (g.FnScore2 >= 0x10000) Hook.InstallForceReturnHook(h, g.FnScore2, FlagAddr(17), SCORE);

        foreach (var f in feats) { Hook.WriteI32(h, FlagAddr(f.Flag), 0); f.On = false; }
        int ready = feats.FindAll(x => x.Ready).Count;
        SetStatus("✓ 已连接 pid=" + pid + "   已就绪 " + ready + "/" + feats.Count + " 个功能   勾选即生效");
    }

    // ---------------------------------------------------------------- 热键 + 延迟安装
    void OnTick(object s, EventArgs e)
    {
        if (h == IntPtr.Zero || feats.Count == 0) return;

        // 延迟安装：未 JIT 的每 3 秒重试
        if (Environment.TickCount64 - lastRetry > 3000)
        {
            lastRetry = Environment.TickCount64;
            bool any = false;
            foreach (var f in feats) if (!f.Ready) { TryInstall(f); any = true; }
            if (any) { RefreshRows(); }
        }

        if (wasDown == null || wasDown.Length != feats.Count) wasDown = new bool[feats.Count];
        for (int i = 0; i < feats.Count; i++)
        {
            var f = feats[i];
            bool down = Down(f.Vk) && (!f.Ctrl || Down(VK_CONTROL));
            if (down && !wasDown[i] && f.Ready)
            {
                f.On = !f.On;
                Hook.WriteI32(h, FlagAddr(f.Flag), f.On ? 1 : 0);
                UpdateRow(f);
            }
            wasDown[i] = down;
        }
    }

    void TryInstall(Feat f)
    {
        g.InvalidateTypes();
        g.Resolve();

        // 伤害三件套：共用同一个挂点，装一次就够
        if ((f.Name == "超级伤害 9999" || f.Name == "伤害倍率 ×10" || f.Name == "防御倍率 ÷4") && !dmgInstalled)
        {
            var md = g.Methods(Game.T_HOOK, "ModifyDamage");
            if (md.Count > 0)
            {
                g.FnModifyDamage1 = md[0];
                if (md.Count > 1) g.FnModifyDamage2 = md[1];
                var d1 = Hook.InstallModifyDamageHook(h, md[0], g.Creature, FlagAddr(10), FlagAddr(11), FlagAddr(12));
                if (d1 != null)
                {
                    extraPatches.Add((md[0], d1));
                    if (md.Count > 1)
                    {
                        var d2 = Hook.InstallModifyDamageHook(h, md[1], g.Creature, FlagAddr(10), FlagAddr(11), FlagAddr(12));
                        if (d2 != null) extraPatches.Add((md[1], d2));
                    }
                    dmgInstalled = true;
                    foreach (var n in new[] { "超级伤害 9999", "伤害倍率 ×10", "防御倍率 ÷4" })
                    {
                        var ff = feats.Find(x => x.Name == n);
                        if (ff != null) { ff.Addr = md[0]; ff.Orig = d1; ff.Note = ""; Hook.WriteI32(h, FlagAddr(ff.Flag), ff.On ? 1 : 0); }
                    }
                }
            }
            return;
        }

        ulong addr = f.Name switch
        {
            "无限血" => g.Method(Game.T_CREATURE, "set_CurrentHp"),
            "金币锁 9999999" => g.Method(Game.T_PLAYER, "set_Gold"),
            "无限能量" => g.Method(Game.T_PCS, "LoseEnergy"),
            "无限星星" => g.Method(Game.T_PCS, "LoseStars"),
            "奖励必给药水" => g.Method(Game.T_POTIONODDS, "Roll"),
            "奖励卡必升级" => g.Method(Game.T_HOOK, "ModifyCardRewardUpgradeOdds"),
            "奖励最高稀有度" => g.Method(Game.T_RARITYODDS, "GetBaseOdds"),
            "未知点必宝箱" => g.Method(Game.T_UNKNOWNODDS, "Roll"),
            "敌人不额外行动" => g.Method(Game.T_HOOK, "ShouldTakeExtraTurn"),
            "药水槽 20" => g.Method(Game.T_PLAYER, "SetMaxPotionCountInternal"),
            "商店免费" => g.Method(Game.T_HOOK, "ModifyMerchantPrice"),
            "最大能量 10" => g.Method(Game.T_HOOK, "ModifyMaxEnergy"),
            "金币奖励 ×2000" => g.Method(Game.T_ENCOUNTER, "get_MinGoldReward"),
            "最高分数" => g.Method(Game.T_SCOREUTIL, "CalculateScore"),
            _ => 0
        };
        if (addr < 0x10000) return;

        byte[] orig = f.Name switch
        {
            "无限血" => Hook.InstallSetterHook(h, addr, g.Creature, HP, FlagAddr(f.Flag)),
            "金币锁 9999999" => Hook.InstallSetterHook(h, addr, g.Player, GOLD, FlagAddr(f.Flag)),
            "无限能量" => Hook.InstallDecimalConstHook(h, addr, g.Pcs, FlagAddr(f.Flag), 0m),
            "无限星星" => Hook.InstallDecimalConstHook(h, addr, g.Pcs, FlagAddr(f.Flag), 0m),
            "奖励必给药水" => Hook.InstallForceReturnHook(h, addr, FlagAddr(f.Flag), 1),
            "奖励卡必升级" => Hook.InstallForceDecimalReturnHook(h, addr, FlagAddr(f.Flag), UPGRADE_ODDS),
            "奖励最高稀有度" => Hook.InstallForceFloatReturnHook(h, addr, FlagAddr(f.Flag), RARITY),
            "未知点必宝箱" => Hook.InstallForceReturnHook(h, addr, FlagAddr(f.Flag), TREASURE),
            "敌人不额外行动" => Hook.InstallForceReturnHook(h, addr, FlagAddr(f.Flag), 0),
            "药水槽 20" => Hook.InstallSetterHook(h, addr, 0, POTION_SLOTS, FlagAddr(f.Flag)),
            "商店免费" => Hook.InstallForceDecimalReturnHook(h, addr, FlagAddr(f.Flag), MERCHANT_FREE),
            "最大能量 10" => Hook.InstallForceDecimalReturnHook(h, addr, FlagAddr(f.Flag), MAX_ENERGY),
            "金币奖励 ×2000" => Hook.InstallForceReturnHook(h, addr, FlagAddr(f.Flag), GOLD_REWARD),
            "最高分数" => Hook.InstallForceReturnHook(h, addr, FlagAddr(f.Flag), SCORE),
            _ => null
        };
        if (orig != null) { f.Addr = addr; f.Orig = orig; f.Note = ""; Hook.WriteI32(h, FlagAddr(f.Flag), f.On ? 1 : 0); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            if (h != IntPtr.Zero)
            {
                foreach (var f in feats) Hook.Restore(h, f.Addr, f.Orig);
                foreach (var p in extraPatches) Hook.Restore(h, p.addr, p.orig);
            }
        }
        catch { }
        base.OnFormClosing(e);
    }
}
