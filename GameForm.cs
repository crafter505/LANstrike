using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LANStrike;

public sealed class GameForm : Form
{
    const int Port = 27020, RW = 480, RH = 270;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 8 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly HashSet<Keys> keys = new();
    readonly Random rng = new();
    readonly Bitmap frame = new(RW, RH);
    readonly float[] zbuffer = new float[RW];
    readonly List<Bot> bots = new();
    readonly List<Grenade> grenades = new();
    readonly List<Vector2> grenadePickups = new();
    readonly Dictionary<int, Remote> remotes = new();
    readonly Dictionary<int, IPEndPoint> peers = new();
    readonly List<string> feed = new();
    readonly List<string> consoleHistory = new();
    readonly object netLock = new();

    Panel menu = null!, pauseMenu = null!, settingsPanel = null!, consolePanel = null!; ComboBox mapBox = null!; NumericUpDown botCount = null!;
    RichTextBox consoleOutput = null!; TextBox consoleInput = null!;
    TrackBar sensitivityBar = null!; CheckBox fullscreenBox = null!;
    TextBox nameBox = null!, ipBox = null!; Label status = null!;
    char[][] map = Array.Empty<char[]>();
    bool playing, paused, consoleOpen, godMode, mouseLocked, firing, reloading, showScores, host;
    int mapId, playerId = 1, hp = 100, armor = 50, kills, deaths, weapon, grenadesHeld = 2;
    int[] ammo = { 12, 0, 30, 36, 6, 5 };
    int[] reserve = { 60, 0, 180, 216, 30, 25 };
    float px = 3.5f, py = 3.5f, angle, pitch, bob, recoil, flash, hurtFlash, fireCooldown, reloadTimer, matchTime = 600, mouseSensitivity = .0023f, viewFov = 1.12f, crossX, crossY;
    long lastTicks;
    Point lastMouse;
    UdpClient? udp; CancellationTokenSource? netCts; IPEndPoint? server;
    readonly Weapon[] weapons = {
        new("1  P9 PISTOL",12,.28f,26,.018f), new("2  RAZOR KNIFE",0,.42f,55,.035f),
        new("3  AR-4 RIFLE",30,.095f,19,.032f), new("4  VX-9 SMG",36,.066f,13,.025f),
        new("5  SG-8 SHOTGUN",6,.72f,12,.11f), new("6  LYNX SNIPER",5,1.05f,96,.14f)
    };

    static readonly string[][] Maps = {
        new[]{"########################","#..........#...........#","#..##......#.....###...#","#..##............###...#","#..........#...........#","#.....###..#..##.......#","#.....###.....##.......#","#.............##.......#","#####..######..........#","#......................#","#..###.........######..#","#..###.................#","#........##............#","#........##.....###....#","#..####.........###....#","#...............###....#","#......######..........#","#......................#","#..###.....####....###.#","#..###.............###.#","#..........####........#","#......................#","#......................#","########################"},
        new[]{"########################","#......................#","#..####..........####..#","#..####..........####..#","#..........##..........#","#..........##..........#","#...###....##....###...#","#...###..........###...#","#...###..........###...#","#..........##..........#","#####......##......#####","#......................#","#......................#","#####......##......#####","#..........##..........#","#...###..........###...#","#...###..........###...#","#...###....##....###...#","#..........##..........#","#..........##..........#","#..####..........####..#","#..####..........####..#","#......................#","########################"},
        new[]{"########################","#......................#","#..######......######..#","#......................#","#.....##........##.....#","#.....##........##.....#","#.....##..####..##.....#","#.........####.........#","####......####......####","#......................#","#..####............##..#","#..####..########..##..#","#........########......#","#..##....########..##..#","#..##............####..#","#......................#","####......####......####","#.........####.........#","#.....##..####..##.....#","#.....##........##.....#","#.....##........##.....#","#..######......######..#","#......................#","########################"}
    };

    public GameForm()
    {
        Text = "LAN//STRIKE"; ClientSize = new Size(1280,720); MinimumSize = new Size(960,540);
        BackColor = Color.Black; DoubleBuffered = true; KeyPreview = true; StartPosition = FormStartPosition.CenterScreen;
        BuildMenu();
        BuildPauseMenu();
        BuildConsole();
        timer.Tick += Tick; timer.Start();
        KeyDown += (_,e)=> { if(playing&&(e.KeyCode==Keys.Oemtilde||e.KeyCode==Keys.F10)){e.SuppressKeyPress=true;ToggleConsole();return;} if(consoleOpen){if(e.KeyCode==Keys.Escape){e.SuppressKeyPress=true;ToggleConsole();}return;} if(e.KeyCode==Keys.Escape&&playing){TogglePause();return;} keys.Add(e.KeyCode); if(e.KeyCode==Keys.Tab) showScores=true; if(e.KeyCode==Keys.R) StartReload(); if(e.KeyCode==Keys.G) ThrowGrenade(); if(e.KeyCode>=Keys.D1&&e.KeyCode<=Keys.D6) SelectWeapon((int)e.KeyCode-(int)Keys.D1); };
        KeyUp += (_,e)=> { keys.Remove(e.KeyCode); if(e.KeyCode==Keys.Tab) showScores=false; };
        MouseDown += (_,e)=> { if(!playing)return; if(!mouseLocked){LockMouse();return;} if(e.Button==MouseButtons.Left) firing=true; };
        MouseUp += (_,e)=> { if(e.Button==MouseButtons.Left) firing=false; };
        MouseMove += MouseLook; FormClosed += (_,__)=> StopNetwork();
    }

    void BuildMenu()
    {
        menu = new Panel { Dock=DockStyle.Fill, BackColor=Color.FromArgb(14,22,29) }; Controls.Add(menu);
        var card = new Panel { Size=new Size(520,610), BackColor=Color.FromArgb(25,38,48) }; menu.Controls.Add(card);
        menu.Resize += (_,__)=> card.Location=new Point((menu.Width-card.Width)/2,(menu.Height-card.Height)/2);
        card.Paint += (_,e)=> { using var p=new Pen(Color.FromArgb(45,104,125),2); e.Graphics.DrawRectangle(p,1,1,card.Width-3,card.Height-3); };
        var title = L("LAN//STRIKE",32,Color.FromArgb(77,216,255)); title.SetBounds(35,24,450,55); title.TextAlign=ContentAlignment.MiddleCenter; card.Controls.Add(title);
        var sub=L("原生 Windows · 离线 / 局域网 FPS",11,Color.FromArgb(150,175,186)); sub.SetBounds(35,78,450,28); sub.TextAlign=ContentAlignment.MiddleCenter; card.Controls.Add(sub);
        card.Controls.Add(L("代号",10,Color.White,45,125,100,24)); nameBox=T("Rookie",45,150,430,38); card.Controls.Add(nameBox);
        card.Controls.Add(L("地图",10,Color.White,45,200,100,24)); mapBox=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Location=new Point(45,225),Size=new Size(430,38),Font=new Font("Segoe UI",11)}; mapBox.Items.AddRange(new object[]{"货运码头 · DOCKYARD","沙漠哨站 · OUTPOST","极地仓库 · FROSTLINE"}); mapBox.SelectedIndex=0; card.Controls.Add(mapBox);
        card.Controls.Add(L("BOT 数量",10,Color.White,45,279,120,24)); botCount=new NumericUpDown{Minimum=0,Maximum=16,Value=7,Location=new Point(165,276),Size=new Size(100,32),Font=new Font("Segoe UI",11)}; card.Controls.Add(botCount);
        var solo=B("开始单人游戏",45,325,430,52,Color.FromArgb(20,126,163)); solo.Click+=(_,__)=>StartGame(false,false); card.Controls.Add(solo);
        var hostBtn=B("建立 LAN 房间",45,390,205,48,Color.FromArgb(32,118,78)); hostBtn.Click+=(_,__)=>StartGame(true,false); card.Controls.Add(hostBtn);
        var joinBtn=B("加入房间",270,390,205,48,Color.FromArgb(97,73,151)); joinBtn.Click+=(_,__)=>StartGame(false,true); card.Controls.Add(joinBtn);
        ipBox=T("127.0.0.1",45,454,430,38); card.Controls.Add(ipBox);
        status=L("WASD 移动  ·  鼠标瞄准  ·  左键射击  ·  R 换弹  ·  1/2/3 切枪",9,Color.FromArgb(134,157,168)); status.SetBounds(35,510,450,62); status.TextAlign=ContentAlignment.MiddleCenter; card.Controls.Add(status);
    }

    void BuildPauseMenu()
    {
        pauseMenu=new Panel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(225,8,14,19),Visible=false};Controls.Add(pauseMenu);
        var card=new Panel{Size=new Size(430,520),BackColor=Color.FromArgb(28,42,52)};pauseMenu.Controls.Add(card);
        pauseMenu.Resize+=(_,__)=>card.Location=new Point((pauseMenu.Width-card.Width)/2,(pauseMenu.Height-card.Height)/2);
        var t=L("游戏暂停",28,Color.FromArgb(77,216,255));t.SetBounds(35,24,360,55);t.TextAlign=ContentAlignment.MiddleCenter;card.Controls.Add(t);
        var resume=B("继续游戏",55,96,320,48,Color.FromArgb(20,126,163));resume.Click+=(_,__)=>TogglePause();card.Controls.Add(resume);
        card.Controls.Add(L("鼠标灵敏度",10,Color.White,55,168,150,26));
        sensitivityBar=new TrackBar{Minimum=1,Maximum=20,Value=8,TickFrequency=1,Location=new Point(50,198),Size=new Size(330,48),BackColor=card.BackColor};
        sensitivityBar.ValueChanged+=(_,__)=>mouseSensitivity=.00065f+sensitivityBar.Value*.00022f;card.Controls.Add(sensitivityBar);
        fullscreenBox=new CheckBox{Text="全屏模式",ForeColor=Color.White,Font=new Font("Segoe UI",10,FontStyle.Bold),Location=new Point(58,260),Size=new Size(160,32),BackColor=Color.Transparent};
        fullscreenBox.CheckedChanged+=(_,__)=>{FormBorderStyle=fullscreenBox.Checked?FormBorderStyle.None:FormBorderStyle.Sizable;WindowState=fullscreenBox.Checked?FormWindowState.Maximized:FormWindowState.Normal;};card.Controls.Add(fullscreenBox);
        var hint=L("视角：鼠标  ·  投雷：G  ·  武器：1—6  ·  2 是刀",9,Color.FromArgb(155,177,187));hint.SetBounds(50,310,330,45);hint.TextAlign=ContentAlignment.MiddleCenter;card.Controls.Add(hint);
        var main=B("回到主界面",55,375,320,48,Color.FromArgb(145,55,55));main.Click+=(_,__)=>ReturnToMenu();card.Controls.Add(main);
        var quit=B("退出游戏",55,435,320,42,Color.FromArgb(61,70,75));quit.Click+=(_,__)=>Close();card.Controls.Add(quit);
        pauseMenu.BringToFront();
    }

    void TogglePause()
    {
        if(!playing)return;paused=!paused;pauseMenu.Visible=paused;pauseMenu.BringToFront();
        if(paused)UnlockMouse();else{pauseMenu.Hide();lastTicks=clock.ElapsedTicks;Focus();}
    }

    void ReturnToMenu()
    {
        StopNetwork();udp=null;server=null;host=false;playing=false;paused=false;firing=false;grenades.Clear();bots.Clear();remotes.Clear();
        pauseMenu.Hide();consolePanel.Hide();consoleOpen=false;menu.Show();menu.BringToFront();UnlockMouse();Invalidate();
    }

    void BuildConsole()
    {
        consolePanel=new Panel{Size=new Size(780,390),BackColor=Color.FromArgb(245,4,10,13),Visible=false};Controls.Add(consolePanel);
        Resize+=(_,__)=>consolePanel.Location=new Point((ClientSize.Width-consolePanel.Width)/2,45);
        consolePanel.Location=new Point((ClientSize.Width-consolePanel.Width)/2,45);
        var title=L("LAN//STRIKE  DEVELOPER CONSOLE",12,Color.FromArgb(75,216,255));title.SetBounds(14,10,600,28);consolePanel.Controls.Add(title);
        consoleOutput=new RichTextBox{ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Color.FromArgb(4,10,13),ForeColor=Color.FromArgb(180,230,195),Font=new Font("Consolas",10),Location=new Point(14,42),Size=new Size(752,292),ScrollBars=RichTextBoxScrollBars.Vertical};consolePanel.Controls.Add(consoleOutput);
        consoleInput=new TextBox{BorderStyle=BorderStyle.FixedSingle,BackColor=Color.FromArgb(17,30,35),ForeColor=Color.White,Font=new Font("Consolas",11),Location=new Point(14,344),Size=new Size(752,30)};consolePanel.Controls.Add(consoleInput);
        consoleInput.KeyDown+=(_,e)=>{if(e.KeyCode!=Keys.Enter)return;e.SuppressKeyPress=true;var command=consoleInput.Text.Trim();consoleInput.Clear();if(command.Length>0){ConsoleWrite("> "+command);ExecuteCommand(command);}};
        ConsoleWrite("输入 help 查看指令。使用 ~ 或 F10 关闭控制台。");consolePanel.BringToFront();
    }

    void ToggleConsole()
    {
        if(!playing||paused)return;consoleOpen=!consoleOpen;keys.Clear();firing=false;
        if(consoleOpen){UnlockMouse();consolePanel.Show();consolePanel.BringToFront();consoleInput.Focus();}else{consolePanel.Hide();Focus();}
    }

    void ConsoleWrite(string text)
    {
        if(consoleOutput==null)return;consoleOutput.AppendText(text+Environment.NewLine);consoleOutput.SelectionStart=consoleOutput.TextLength;consoleOutput.ScrollToCaret();
    }

    void ExecuteCommand(string command)
    {
        var parts=command.Split(' ',StringSplitOptions.RemoveEmptyEntries);if(parts.Length==0)return;var cmd=parts[0].ToLowerInvariant();
        int Number(int fallback,int min,int max){if(parts.Length<2||!int.TryParse(parts[1],out int n))return fallback;return Math.Clamp(n,min,max);}
        switch(cmd){
            case "help":ConsoleWrite("help | clear | heal | armor [0-100] | god");ConsoleWrite("give [1-6/all] | grenades [0-9] | sensitivity [1-20]");ConsoleWrite("fov [65-115] | time [秒] | status | bots add [数量] | bots clear");break;
            case "clear":consoleOutput.Clear();break;
            case "heal":hp=100;ConsoleWrite("生命已恢复。 ");break;
            case "armor":armor=Number(100,0,100);ConsoleWrite("护甲 = "+armor);break;
            case "god":godMode=!godMode;ConsoleWrite("无敌模式 "+(godMode?"ON":"OFF"));break;
            case "grenades":grenadesHeld=Number(3,0,9);ConsoleWrite("瞬爆雷 = "+grenadesHeld);break;
            case "sensitivity":var sens=Number(8,1,20);sensitivityBar.Value=sens;ConsoleWrite("鼠标灵敏度 = "+sens);break;
            case "fov":var deg=Number(90,65,115);viewFov=deg*MathF.PI/180f;ConsoleWrite("FOV = "+deg);break;
            case "time":matchTime=Number(600,0,7200);ConsoleWrite("剩余时间 = "+(int)matchTime+" 秒");break;
            case "give":if(parts.Length>1&&parts[1].ToLowerInvariant()!="all"&&int.TryParse(parts[1],out int slot)&&slot>=1&&slot<=6){reserve[slot-1]=999;ammo[slot-1]=weapons[slot-1].Clip;ConsoleWrite("已补充 "+weapons[slot-1].Name);}else{for(int i=0;i<6;i++){reserve[i]=999;ammo[i]=weapons[i].Clip;}ConsoleWrite("全部武器弹药已补满。");}break;
            case "status":ConsoleWrite($"模式: {(host?"LAN HOST":server!=null?"LAN CLIENT":"SINGLE")}  HP:{hp}  K:{kills} D:{deaths}  LAN玩家:{remotes.Count+1}");break;
            case "bots":if(host||server!=null){ConsoleWrite("LAN 房间禁止 Bot。");break;}if(parts.Length>1&&parts[1].ToLowerInvariant()=="clear"){bots.Clear();ConsoleWrite("Bot 已清空。");}else if(parts.Length>1&&parts[1].ToLowerInvariant()=="add"){int count=parts.Length>2&&int.TryParse(parts[2],out int bc)?Math.Clamp(bc,1,16):1;for(int i=0;i<count;i++)SpawnBot(bots.Count+i);ConsoleWrite("已增加 "+count+" 个 Bot。");}else ConsoleWrite("用法: bots add [1-16] 或 bots clear");break;
            default:ConsoleWrite("未知指令："+cmd+"。输入 help 查看列表。");break;
        }
    }

    static Label L(string t,float s,Color c,int x=0,int y=0,int w=100,int h=24)=>new(){Text=t,ForeColor=c,Font=new Font("Segoe UI",s,FontStyle.Bold),Location=new Point(x,y),Size=new Size(w,h),BackColor=Color.Transparent};
    static TextBox T(string t,int x,int y,int w,int h)=>new(){Text=t,Location=new Point(x,y),Size=new Size(w,h),Font=new Font("Segoe UI",11),BackColor=Color.FromArgb(236,242,244),BorderStyle=BorderStyle.FixedSingle};
    static Button B(string t,int x,int y,int w,int h,Color c)=>new(){Text=t,Location=new Point(x,y),Size=new Size(w,h),FlatStyle=FlatStyle.Flat,BackColor=c,ForeColor=Color.White,Font=new Font("Segoe UI",11,FontStyle.Bold),Cursor=Cursors.Hand};

    void StartGame(bool asHost,bool asClient)
    {
        mapId=mapBox.SelectedIndex; map=Maps[mapId].Select(s=>s.ToCharArray()).ToArray(); host=asHost; playerId=asHost?1:rng.Next(1000,999999);
        px=2.5f; py=2.5f; hp=100; armor=50; kills=deaths=0; matchTime=600; weapon=2; grenadesHeld=2; paused=false; ammo=new[]{12,0,30,36,6,5}; reserve=new[]{60,0,180,216,30,25}; bots.Clear(); grenades.Clear(); grenadePickups.Clear(); remotes.Clear();
        for(int i=0;i<5;i++) grenadePickups.Add(RandomOpen(4));
        if(!asHost&&!asClient) for(int i=0;i<(int)botCount.Value;i++) SpawnBot(i);
        playing=true; consoleOpen=false; godMode=false; crossX=crossY=0; consolePanel.Hide(); menu.Hide(); mouseLocked=false; firing=false; Focus(); lastTicks=clock.ElapsedTicks;
        if(asHost) StartHost(); else if(asClient) StartClient(ipBox.Text.Trim());
        Invalidate();
    }

    void SpawnBot(int i)
    {
        var p=RandomOpen(8); bots.Add(new Bot { Id=-(i+1),Name=new[]{"Viper","Echo","Rook","Nova","Mako","Jinx","Bolt","Ghost","Iris","Knox"}[i%10],X=p.X,Y=p.Y,Angle=(float)rng.NextDouble()*6.28f,Hp=100,Think=(float)rng.NextDouble(),Strafe=rng.Next(2)==0?-1:1 });
    }
    Vector2 RandomOpen(int away=0){ for(int i=0;i<500;i++){int x=rng.Next(1,23),y=rng.Next(1,23);if(map[y][x]=='.'&&Vector2.Distance(new(px,py),new(x+.5f,y+.5f))>away)return new(x+.5f,y+.5f);}return new(20.5f,20.5f); }

    void Tick(object? sender,EventArgs e)
    {
        if(!playing)return; long now=clock.ElapsedTicks; float dt=Math.Clamp((now-lastTicks)/(float)Stopwatch.Frequency,.001f,.04f); lastTicks=now;
        if(paused){Invalidate();return;}
        UpdateMouseLook();
        UpdatePlayer(dt); UpdateBots(dt); UpdateGrenades(dt); UpdateEffects(dt); if(host||server!=null) NetworkTick(dt); Render(); Invalidate();
    }

    void UpdatePlayer(float dt)
    {
        if(hp<=0)return; float speed=(keys.Contains(Keys.ShiftKey)?4.8f:3.25f)*dt; float dx=0,dy=0;
        if(keys.Contains(Keys.W)){dx+=MathF.Cos(angle)*speed;dy+=MathF.Sin(angle)*speed;} if(keys.Contains(Keys.S)){dx-=MathF.Cos(angle)*speed;dy-=MathF.Sin(angle)*speed;}
        if(keys.Contains(Keys.A)){dx+=MathF.Sin(angle)*speed;dy-=MathF.Cos(angle)*speed;} if(keys.Contains(Keys.D)){dx-=MathF.Sin(angle)*speed;dy+=MathF.Cos(angle)*speed;}
        Move(ref px,ref py,dx,dy,.24f); if(MathF.Abs(dx)+MathF.Abs(dy)>.001f)bob+=dt*(keys.Contains(Keys.ShiftKey)?13:9);
        if(firing&&mouseLocked) Fire();
    }

    void UpdateBots(float dt)
    {
        foreach(var b in bots.ToArray()){
            if(b.Hp<=0){b.Respawn-=dt;if(b.Respawn<=0){var p=RandomOpen(7);b.X=p.X;b.Y=p.Y;b.Hp=100;b.Awareness=0;b.Cooldown=1.2f;}continue;}
            float dx=px-b.X,dy=py-b.Y,dist=MathF.Sqrt(dx*dx+dy*dy),playerAngle=MathF.Atan2(dy,dx);bool canSee=dist<9.5f&&LineClear(b.X,b.Y,px,py);
            if(canSee)b.Awareness=MathF.Min(1.4f,b.Awareness+dt);else b.Awareness=MathF.Max(0,b.Awareness-dt*1.6f);
            b.Angle=TurnToward(b.Angle,playerAngle+b.AimError,dt*1.25f);b.Think-=dt;
            float mx=0,my=0;if(dist>3.5f){mx=MathF.Cos(b.Angle)*dt*1.35f;my=MathF.Sin(b.Angle)*dt*1.35f;}else{mx=MathF.Cos(b.Angle+1.57f*b.Strafe)*dt*.85f;my=MathF.Sin(b.Angle+1.57f*b.Strafe)*dt*.85f;}
            Move(ref b.X,ref b.Y,mx,my,.22f);b.Cooldown-=dt;
            if(canSee&&b.Awareness>.9f&&MathF.Abs(AngleDiff(b.Angle,playerAngle))<.19f&&b.Cooldown<=0){b.Cooldown=.9f+(float)rng.NextDouble()*.75f;double chance=dist<4? .48:.28;if(rng.NextDouble()<chance)DamagePlayer(rng.Next(4,10),b.Name);b.AimError=((float)rng.NextDouble()-.5f)*.34f;}
            if(b.Think<=0){b.Think=.55f+(float)rng.NextDouble()*.9f;b.AimError=((float)rng.NextDouble()-.5f)*.28f;if(rng.NextDouble()<.45)b.Strafe=-b.Strafe;}
        }
    }

    void Fire()
    {
        var w=weapons[weapon];float aimAngle=angle+crossX/RW*viewFov;if(reloading||fireCooldown>0)return;
        if(weapon==1){fireCooldown=w.Delay;recoil=.08f;flash=.045f;Bot? hit=null;float near=1.75f;foreach(var bot in bots){if(bot.Hp<=0)continue;float d=Vector2.Distance(new(px,py),new(bot.X,bot.Y));if(d<near&&MathF.Abs(AngleDiff(aimAngle,MathF.Atan2(bot.Y-py,bot.X-px)))<.68f&&LineClear(px,py,bot.X,bot.Y)){hit=bot;near=d;}}if(hit!=null)HitBot(hit,w.Damage);return;}
        if(ammo[weapon]<=0){StartReload();return;}ammo[weapon]--;fireCooldown=w.Delay;flash=.07f;recoil=MathF.Min(recoil+w.Kick,.15f);
        int pellets=weapon==4?8:1;float spread=weapon switch{3=>.045f,4=>.18f,5=>.004f,_=>.018f};
        for(int p=0;p<pellets;p++){float shot=aimAngle+((float)rng.NextDouble()-.5f)*spread;Bot? best=null;float bd=999;
            foreach(var bot in bots){if(bot.Hp<=0)continue;float d=Vector2.Distance(new(px,py),new(bot.X,bot.Y));float aim=MathF.Abs(AngleDiff(shot,MathF.Atan2(bot.Y-py,bot.X-px)));if(aim<MathF.Atan(.32f/MathF.Max(d,.1f))&&d<bd&&LineClear(px,py,bot.X,bot.Y)){best=bot;bd=d;}}
            if(best!=null)HitBot(best,w.Damage);}
        if(server!=null)Send(new Packet{Type="shot",Id=playerId,Name=Name,X=px,Y=py,A=aimAngle,Weapon=weapon});
    }

    void HitBot(Bot bot,int damage)
    {
        if(bot.Hp<=0)return;bot.Hp-=damage;if(bot.Hp<=0){kills++;bot.Deaths++;bot.Respawn=3;AddFeed(Name+"  ▸  "+bot.Name);}
    }

    void ThrowGrenade()
    {
        if(!playing||paused||grenadesHeld<=0)return;grenadesHeld--;grenades.Add(new Grenade{X=px+MathF.Cos(angle)*.45f,Y=py+MathF.Sin(angle)*.45f,VX=MathF.Cos(angle)*10,VY=MathF.Sin(angle)*10,Life=.34f});
    }

    void UpdateGrenades(float dt)
    {
        for(int i=grenades.Count-1;i>=0;i--){var gr=grenades[i];gr.Life-=dt;float nx=gr.X+gr.VX*dt,ny=gr.Y+gr.VY*dt;if(!Cell(nx,ny)||gr.Life<=0){Explode(gr.X,gr.Y);grenades.RemoveAt(i);}else{gr.X=nx;gr.Y=ny;gr.VX*=MathF.Pow(.18f,dt);gr.VY*=MathF.Pow(.18f,dt);}}
        for(int i=0;i<grenadePickups.Count;i++)if(Vector2.Distance(new(px,py),grenadePickups[i])<.65f&&grenadesHeld<3){grenadesHeld++;grenadePickups[i]=RandomOpen(5);AddFeed("拾取瞬爆雷  G");}
    }

    void Explode(float x,float y)
    {
        flash=.18f;AddFeed("轰！瞬爆雷");foreach(var bot in bots){if(bot.Hp<=0)continue;float d=Vector2.Distance(new(x,y),new(bot.X,bot.Y));if(d<4.8f&&LineClear(x,y,bot.X,bot.Y))HitBot(bot,(int)(115*(1-d/5.5f)));}float self=Vector2.Distance(new(x,y),new(px,py));if(self<3.2f)DamagePlayer((int)(65*(1-self/3.5f)),Name);
    }

    void DamagePlayer(int amount,string attacker)
    {
        if(godMode)return;
        int absorb=Math.Min(armor,amount/2);armor-=absorb;hp-=amount-absorb;hurtFlash=.22f;
        if(hp<=0){deaths++;AddFeed(attacker+"  ▸  "+Name);var p=RandomOpen();px=p.X;py=p.Y;hp=100;armor=25;}
    }
    void StartReload(){if(weapon==1||reloading||ammo[weapon]>=weapons[weapon].Clip||reserve[weapon]<=0)return;reloading=true;reloadTimer=weapon==2?1.25f:1.55f;}
    void FinishReload(){int need=weapons[weapon].Clip-ammo[weapon],take=Math.Min(need,reserve[weapon]);ammo[weapon]+=take;reserve[weapon]-=take;reloading=false;}
    void SelectWeapon(int w){if(w<0||w>2)return;weapon=w;reloading=false;fireCooldown=.2f;}
    void UpdateEffects(float dt){crossX*=MathF.Pow(.08f,dt);crossY*=MathF.Pow(.08f,dt);fireCooldown=MathF.Max(0,fireCooldown-dt);flash=MathF.Max(0,flash-dt);hurtFlash=MathF.Max(0,hurtFlash-dt);recoil=MathF.Max(0,recoil-dt*.7f);if(reloading){reloadTimer-=dt;if(reloadTimer<=0)FinishReload();}matchTime=MathF.Max(0,matchTime-dt);}

    void Move(ref float x,ref float y,float dx,float dy,float r){if(IsOpen(x+dx,y,r))x+=dx;if(IsOpen(x,y+dy,r))y+=dy;}
    bool IsOpen(float x,float y,float r)=>Cell(x-r,y-r)&&Cell(x+r,y-r)&&Cell(x-r,y+r)&&Cell(x+r,y+r);
    bool Cell(float x,float y){int ix=(int)x,iy=(int)y;return iy>=0&&iy<map.Length&&ix>=0&&ix<map[0].Length&&map[iy][ix]!='#';}
    bool LineClear(float x1,float y1,float x2,float y2){float d=Vector2.Distance(new(x1,y1),new(x2,y2));int n=(int)(d*8);for(int i=1;i<n;i++){float t=i/(float)n;if(!Cell(x1+(x2-x1)*t,y1+(y2-y1)*t))return false;}return true;}
    static float AngleDiff(float a,float b){float d=(b-a+MathF.PI)%(2*MathF.PI)-MathF.PI;return d<-MathF.PI?d+2*MathF.PI:d;}
    static float TurnToward(float a,float b,float max){float d=AngleDiff(a,b);return a+Math.Clamp(d,-max,max);}

    void Render()
    {
        using var g=Graphics.FromImage(frame);g.SmoothingMode=SmoothingMode.None;g.Clear(Color.Black);
        using(var sky=new LinearGradientBrush(new Rectangle(0,0,RW,RH/2),MapColor(0),MapColor(1),90))g.FillRectangle(sky,0,0,RW,RH/2);
        using(var floor=new LinearGradientBrush(new Rectangle(0,RH/2,RW,RH/2),MapColor(2),Color.FromArgb(20,23,25),90))g.FillRectangle(floor,0,RH/2,RW,RH/2);
        float fov=viewFov, horizon=RH/2+pitch+(MathF.Sin(bob)*2); for(int x=0;x<RW;x++){float ray=angle-fov/2+fov*x/RW;float dist=Cast(px,py,ray,out int side,out int tx,out int ty);zbuffer[x]=dist;int h=(int)Math.Min(RH*2,RH/Math.Max(dist,.01f));int top=(int)(horizon-h/2);float shade=Math.Clamp(1-dist/22f,.18f,1);Color baseC=((tx+ty)&1)==0?MapColor(3):MapColor(4);if(side==1)shade*=.72f;using var pen=new Pen(Color.FromArgb((int)(baseC.R*shade),(int)(baseC.G*shade),(int)(baseC.B*shade)));g.DrawLine(pen,x,top,x,top+h);}
        var sprites=new List<(float x,float y,string n,int hp,Color c)>();foreach(var b in bots)if(b.Hp>0)sprites.Add((b.X,b.Y,b.Name,b.Hp,Color.FromArgb(225,75,72)));lock(netLock)foreach(var r in remotes.Values)sprites.Add((r.X,r.Y,r.Name,r.Hp,Color.FromArgb(55,180,238)));
        foreach(var s in sprites.OrderByDescending(s=>Vector2.DistanceSquared(new(px,py),new(s.x,s.y))))DrawSprite(g,s.x,s.y,s.n,s.hp,s.c,fov,horizon);
        foreach(var p in grenadePickups)DrawWorldItem(g,p.X,p.Y,false,fov,horizon);foreach(var gr in grenades)DrawWorldItem(g,gr.X,gr.Y,true,fov,horizon);
        DrawWeapon(g);DrawHud(g);
    }

    float Cast(float ox,float oy,float a,out int side,out int tx,out int ty)
    {
        float dx=MathF.Cos(a),dy=MathF.Sin(a),dist=0;side=0;tx=ty=0;for(int i=0;i<700;i++){dist+=.035f;float x=ox+dx*dist,y=oy+dy*dist;if(!Cell(x,y)){tx=(int)x;ty=(int)y;float fx=x-MathF.Floor(x),fy=y-MathF.Floor(y);side=(MathF.Min(fx,1-fx)>MathF.Min(fy,1-fy))?1:0;return dist*MathF.Cos(a-angle);}}return 30;
    }
    void DrawWorldItem(Graphics g,float x,float y,bool flying,float fov,float horizon)
    {
        float dx=x-px,dy=y-py,dist=MathF.Sqrt(dx*dx+dy*dy),rel=AngleDiff(angle,MathF.Atan2(dy,dx));if(MathF.Abs(rel)>fov*.6f||dist<.2f)return;int sx=(int)(RW/2+rel/fov*RW);if(sx<0||sx>=RW||dist>zbuffer[sx]+.15f)return;int size=Math.Clamp((int)(18/dist),3,18),sy=(int)(horizon+RH/(dist*3.2f)-(flying?size:0));using var glow=new SolidBrush(Color.FromArgb(90,85,255,130));using var core=new SolidBrush(Color.FromArgb(35,58,43));g.FillEllipse(glow,sx-size,sy-size,size*2,size*2);g.FillEllipse(core,sx-size/2,sy-size/2,size,size);g.DrawLine(Pens.LimeGreen,sx,sy-size/2,sx+size/2,sy-size);
    }

    void DrawSprite(Graphics g,float x,float y,string n,int health,Color c,float fov,float horizon)
    {
        float dx=x-px,dy=y-py,dist=MathF.Sqrt(dx*dx+dy*dy),rel=AngleDiff(angle,MathF.Atan2(dy,dx));if(MathF.Abs(rel)>fov*.62f||dist<.2f)return;
        int sx=(int)(RW/2+rel/fov*RW),h=Math.Max(8,(int)(RH/dist)),w=Math.Max(5,(int)(h*.42f)),top=(int)(horizon-h*.62f);if(sx<0||sx>=RW||dist>zbuffer[Math.Clamp(sx,0,RW-1)]+.2f)return;
        int head=Math.Max(3,(int)(h*.18f)),bodyTop=top+head,bodyH=(int)(h*.43f),legTop=bodyTop+bodyH;
        using var suit=new SolidBrush(c);using var dark=new SolidBrush(Color.FromArgb(31,38,43));using var skin=new SolidBrush(Color.FromArgb(213,166,128));using var outline=new Pen(Color.FromArgb(24,28,31),Math.Max(1,h/28f));using var limb=new Pen(c,Math.Max(2,w*.18f));
        g.SmoothingMode=SmoothingMode.AntiAlias;g.FillEllipse(skin,sx-head/2,top,head,head);g.DrawEllipse(outline,sx-head/2,top,head,head);
        var torso=new[]{new Point(sx-w/3,bodyTop),new Point(sx+w/3,bodyTop),new Point(sx+w/4,bodyTop+bodyH),new Point(sx-w/4,bodyTop+bodyH)};g.FillPolygon(suit,torso);g.DrawPolygon(outline,torso);
        g.DrawLine(limb,sx-w/4,bodyTop+3,sx-w/2,bodyTop+bodyH*3/4);g.DrawLine(limb,sx+w/4,bodyTop+3,sx+w/2,bodyTop+bodyH*2/3);
        g.DrawLine(outline,sx+w/3,bodyTop+bodyH/2,sx+w/2,bodyTop+bodyH/2);g.DrawLine(new Pen(dark,Math.Max(2,w*.13f)),sx-w/8,legTop,sx-w/5,top+h);g.DrawLine(new Pen(dark,Math.Max(2,w*.13f)),sx+w/8,legTop,sx+w/5,top+h);g.SmoothingMode=SmoothingMode.None;
        using var white=new SolidBrush(Color.White);using var green=new SolidBrush(Color.FromArgb(69,220,102));g.FillRectangle(white,sx-w/2,top-7,w,3);g.FillRectangle(green,sx-w/2,top-7,(int)(w*Math.Clamp(health/100f,0,1)),3);
    }

    void DrawWeapon(Graphics g)
    {
        int cx=RW/2,y=RH-60+(int)(MathF.Abs(MathF.Sin(bob))*3)+(int)(recoil*180);using var metal=new SolidBrush(Color.FromArgb(51,62,68));using var dark=new SolidBrush(Color.FromArgb(20,25,28));using var accent=new SolidBrush(Color.FromArgb(46,188,218));using var wood=new SolidBrush(Color.FromArgb(104,70,43));using var hand=new SolidBrush(Color.FromArgb(190,139,103));
        switch(weapon){
            case 0:g.FillPolygon(metal,new[]{new Point(cx-16,y),new Point(cx+14,y),new Point(cx+11,RH),new Point(cx-19,RH)});g.FillRectangle(dark,cx-8,y-20,16,28);g.FillRectangle(accent,cx-6,y-18,12,3);break;
            case 1:g.FillEllipse(hand,cx+12,y+18,29,45);using(var blade=new SolidBrush(Color.FromArgb(190,210,216)))g.FillPolygon(blade,new[]{new Point(cx+3,y+23),new Point(cx+70,y-42),new Point(cx+47,y+18),new Point(cx+12,y+42)});g.DrawLine(new Pen(Color.White,2),cx+10,y+22,cx+66,y-37);g.FillRectangle(dark,cx+5,y+27,37,10);break;
            case 2:g.FillPolygon(metal,new[]{new Point(cx-25,y),new Point(cx+28,y),new Point(cx+40,RH),new Point(cx-33,RH)});g.FillRectangle(dark,cx-7,y-35,14,47);g.FillRectangle(accent,cx-5,y-29,10,5);g.FillRectangle(dark,cx+17,y+8,10,40);break;
            case 3:g.FillPolygon(dark,new[]{new Point(cx-27,y+4),new Point(cx+30,y+4),new Point(cx+35,RH),new Point(cx-31,RH)});g.FillRectangle(metal,cx-19,y-25,38,42);g.FillRectangle(accent,cx-14,y-19,28,4);g.FillRectangle(dark,cx-5,y-36,10,18);break;
            case 4:g.FillPolygon(wood,new[]{new Point(cx-35,y+8),new Point(cx+35,y+8),new Point(cx+47,RH),new Point(cx-43,RH)});g.FillRectangle(metal,cx-15,y-30,30,43);g.FillRectangle(dark,cx-11,y-35,22,31);g.FillRectangle(wood,cx-27,y-2,54,13);break;
            case 5:g.FillPolygon(metal,new[]{new Point(cx-31,y+7),new Point(cx+32,y+7),new Point(cx+43,RH),new Point(cx-38,RH)});g.FillRectangle(dark,cx-8,y-48,16,62);g.FillEllipse(dark,cx-16,y-42,32,20);g.FillEllipse(accent,cx-10,y-38,20,12);g.FillRectangle(dark,cx+20,y+17,11,38);break;
        }
        if(flash>0&&weapon!=1){using var f=new SolidBrush(Color.FromArgb(245,255,195,62));g.FillPolygon(f,new[]{new Point(cx,y-52),new Point(cx-12,y-27),new Point(cx,y-34),new Point(cx+12,y-27)});}
    }
    void DrawHud(Graphics g)
    {
        using var font=new Font("Consolas",9,FontStyle.Bold);using var big=new Font("Consolas",14,FontStyle.Bold);using var white=new SolidBrush(Color.White);using var cyan=new SolidBrush(Color.FromArgb(75,216,255));using var shadow=new SolidBrush(Color.FromArgb(160,0,0,0));
        g.FillRectangle(shadow,6,RH-32,188,26);g.DrawString($"HP {hp:000}  ARM {armor:000}",big,white,10,RH-30);g.FillRectangle(shadow,RW-160,RH-38,154,32);g.DrawString(weapons[weapon].Name,font,cyan,RW-154,RH-36);g.DrawString(weapon==1?"MELEE":reloading?"RELOADING":$"{ammo[weapon]:00} / {reserve[weapon]:000}",big,white,RW-102,RH-25);g.DrawString($"G  x{grenadesHeld}",font,cyan,RW-154,RH-14);
        int crossScreenX=Math.Clamp(RW/2+(int)crossX,12,RW-12),crossScreenY=Math.Clamp(RH/2+(int)crossY,12,RH-42);g.DrawLine(Pens.White,crossScreenX-6,crossScreenY,crossScreenX+6,crossScreenY);g.DrawLine(Pens.White,crossScreenX,crossScreenY-6,crossScreenX,crossScreenY+6);if(!mouseLocked&&!paused&&!consoleOpen){using var promptBg=new SolidBrush(Color.FromArgb(190,0,0,0));g.FillRectangle(promptBg,RW/2-105,RH/2-22,210,44);g.DrawString("点击画面控制视角",big,white,RW/2-86,RH/2-12);}g.DrawString($"{(int)matchTime/60:00}:{(int)matchTime%60:00}",big,white,RW/2-28,8);g.DrawString($"K {kills}  D {deaths}",font,white,10,9);
        int fy=28;foreach(var s in feed.Take(4)){g.DrawString(s,font,white,RW-170,fy);fy+=12;}
        if(hurtFlash>0){using var hurt=new SolidBrush(Color.FromArgb((int)(hurtFlash*300),210,20,20));g.FillRectangle(hurt,0,0,RW,RH);}if(showScores)DrawScoreboard(g);
    }
    void DrawScoreboard(Graphics g){using var bg=new SolidBrush(Color.FromArgb(220,10,17,22));g.FillRectangle(bg,90,35,300,195);using var f=new Font("Consolas",10,FontStyle.Bold);g.DrawString("LAN//STRIKE   SCOREBOARD",f,Brushes.Cyan,112,50);g.DrawString($"{Name,-16} {kills,3} K  {deaths,3} D",f,Brushes.White,112,80);int y=102;foreach(var b in bots.OrderByDescending(x=>x.Kills).Take(9)){g.DrawString($"{b.Name,-16} {b.Kills,3} K  {b.Deaths,3} D",f,Brushes.LightGray,112,y);y+=13;}}
    Color MapColor(int i)=>mapId switch{0=>new[]{Color.FromArgb(75,117,142),Color.FromArgb(147,185,199),Color.FromArgb(63,65,59),Color.FromArgb(105,79,57),Color.FromArgb(73,96,94)}[i],1=>new[]{Color.FromArgb(195,126,68),Color.FromArgb(242,185,113),Color.FromArgb(91,67,43),Color.FromArgb(161,108,65),Color.FromArgb(115,80,52)}[i],_=>new[]{Color.FromArgb(98,147,170),Color.FromArgb(196,225,235),Color.FromArgb(63,84,93),Color.FromArgb(128,157,168),Color.FromArgb(76,103,115)}[i]};
    void AddFeed(string s){feed.Insert(0,s);if(feed.Count>6)feed.RemoveAt(6);}
    string Name=>string.IsNullOrWhiteSpace(nameBox.Text)?"Rookie":nameBox.Text.Trim()[..Math.Min(16,nameBox.Text.Trim().Length)];

    void MouseLook(object? s,MouseEventArgs e){}
    void UpdateMouseLook(){if(!playing||paused||!mouseLocked)return;var center=PointToScreen(new Point(ClientSize.Width/2,ClientSize.Height/2));var current=Cursor.Position;int dx=current.X-center.X,dy=current.Y-center.Y;if(dx==0&&dy==0)return;angle+=dx*mouseSensitivity;pitch=Math.Clamp(pitch+dy*mouseSensitivity*43f,-42,42);crossX=Math.Clamp(crossX+dx*.32f,-RW*.28f,RW*.28f);crossY=Math.Clamp(crossY+dy*.32f,-RH*.24f,RH*.24f);Cursor.Position=center;}
    void LockMouse(){if(mouseLocked)return;Activate();Focus();Capture=true;mouseLocked=true;Cursor.Hide();Cursor.Position=PointToScreen(new Point(ClientSize.Width/2,ClientSize.Height/2));}
    void UnlockMouse(){if(!mouseLocked)return;mouseLocked=false;firing=false;Capture=false;Cursor.Show();}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);if(playing){e.Graphics.InterpolationMode=InterpolationMode.NearestNeighbor;e.Graphics.PixelOffsetMode=PixelOffsetMode.Half;e.Graphics.DrawImage(frame,ClientRectangle);}}

    void StartHost(){try{udp=new UdpClient(Port);udp.EnableBroadcast=true;netCts=new();_=ReceiveLoop(netCts.Token);AddFeed("LAN 房间已建立 · UDP 27020");}catch(Exception ex){AddFeed("LAN 启动失败: "+ex.Message);}}
    void StartClient(string ip){try{server=new IPEndPoint(IPAddress.Parse(ip),Port);udp=new UdpClient(0);netCts=new();_=ReceiveLoop(netCts.Token);Send(new Packet{Type="join",Id=playerId,Name=Name,Map=mapId});AddFeed("正在连接 "+ip);}catch(Exception ex){AddFeed("连接失败: "+ex.Message);}}
    async Task ReceiveLoop(CancellationToken ct){if(udp==null)return;while(!ct.IsCancellationRequested)try{var rr=await udp.ReceiveAsync(ct);var p=JsonSerializer.Deserialize<Packet>(rr.Buffer);if(p==null)continue;BeginInvoke(new Action(()=>HandlePacket(p,rr.RemoteEndPoint)));}catch(OperationCanceledException){break;}catch{await Task.Delay(20);}}
    void HandlePacket(Packet p,IPEndPoint from){if(host){if(p.Type=="join"){peers[p.Id]=from;SendTo(new Packet{Type="welcome",Id=1,Map=mapId,Name=Name},from);AddFeed(p.Name+" 加入了房间");}if(p.Type=="state"){lock(netLock)remotes[p.Id]=new Remote(p.Name,p.X,p.Y,p.A,p.Health);foreach(var ep in peers.Where(x=>x.Key!=p.Id).Select(x=>x.Value))SendTo(p,ep);}if(p.Type=="shot")foreach(var ep in peers.Where(x=>x.Key!=p.Id).Select(x=>x.Value))SendTo(p,ep);}else{if(p.Type=="welcome"){mapId=p.Map;map=Maps[mapId].Select(s=>s.ToCharArray()).ToArray();AddFeed("已加入 "+p.Name+" 的房间");}if(p.Type=="state"&&p.Id!=playerId)lock(netLock)remotes[p.Id]=new Remote(p.Name,p.X,p.Y,p.A,p.Health);}}
    float netClock;void NetworkTick(float dt){netClock-=dt;if(netClock>0)return;netClock=.05f;var p=new Packet{Type="state",Id=playerId,Name=Name,X=px,Y=py,A=angle,Health=hp,Weapon=weapon};if(host){foreach(var ep in peers.Values)SendTo(p,ep);foreach(var b in bots)foreach(var ep in peers.Values)SendTo(new Packet{Type="state",Id=b.Id,Name=b.Name,X=b.X,Y=b.Y,A=b.Angle,Health=b.Hp},ep);}else Send(p);}
    void Send(Packet p){if(server!=null)SendTo(p,server);}void SendTo(Packet p,IPEndPoint ep){try{var data=JsonSerializer.SerializeToUtf8Bytes(p);udp?.Send(data,data.Length,ep);}catch{}}
    void StopNetwork(){try{netCts?.Cancel();udp?.Close();}catch{}}

    readonly record struct Weapon(string Name,int Clip,float Delay,int Damage,float Kick);
    sealed class Bot{public int Id,Hp=100,Kills,Deaths,Strafe=1;public string Name="BOT";public float X,Y,Angle,Cooldown,Think,Respawn,Awareness,AimError;}
    readonly record struct Remote(string Name,float X,float Y,float A,int Hp);
    sealed class Grenade{public float X,Y,VX,VY,Life;}
    sealed class Packet{public string Type{get;set;}="";public int Id{get;set;}public string Name{get;set;}="";public float X{get;set;}public float Y{get;set;}public float A{get;set;}public int Map{get;set;}public int Health{get;set;}=100;public int Weapon{get;set;}}
}
