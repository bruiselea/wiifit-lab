using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using BalanceBoard.Core.Models;
using BalanceBoard.Core.Processing;

namespace WiiFitLab;

public partial class TiltMazeWindow : Window
{
    private const double WorldScale = 100;
    private const double BallRadiusWorld = 0.28;
    private const double BallRadiusPhysics = BallRadiusWorld * WorldScale;
    private const double SurfaceY = 0.02;
    private readonly DispatcherTimer _timer;
    private readonly Model3DGroup _boardModels = new();
    private readonly AxisAngleRotation3D _pitchRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _rollRotation = new(new Vector3D(0, 0, 1), 0);
    private readonly List<GameBall> _balls = [];
    private readonly List<HoleDefinition> _holes = [];
    private readonly List<Rect> _barriers = [];
    private TiltMazeInput _boardInput;
    private long _lastFrameAt = Stopwatch.GetTimestamp();
    private long _lastBoardUpdateAt;
    private double _latestBalanceX = 50;
    private double _latestBalanceY = 50;
    private double _centerX = 50;
    private double _centerY = 50;
    private double _visualTiltX;
    private double _visualTiltY;
    private double _remainingSeconds;
    private double _freezeSeconds;
    private int _levelIndex;
    private bool _running;
    private bool _connected;
    private bool _boardControlling;
    private bool _hasCenter;
    private bool _completed;
    private bool _boardButtonWasPressed;
    private bool _hasBoardButtonState;
    private bool _left;
    private bool _right;
    private bool _up;
    private bool _down;

    public TiltMazeWindow()
    {
        InitializeComponent();
        BuildSceneRoot();
        SensitivitySlider.ValueChanged += SensitivitySlider_ValueChanged;
        LoadLevel(0);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, Timer_Tick, Dispatcher);
        _timer.Start();
        Deactivated += (_, _) => ClearKeyboardInput();
        Closed += (_, _) => _timer.Stop();
    }

    public void SetConnectionState(bool connected)
    {
        _connected = connected;
        if (!connected)
        {
            _boardControlling = false;
            SignalStatusText.Text = "キーボード操作（ボード未接続）";
            SignalStatusText.Foreground = BrushFrom("#F0A84A");
        }
    }

    public void Update(ProcessedBalance data)
    {
        _connected = true;
        _lastBoardUpdateAt = Stopwatch.GetTimestamp();
        _latestBalanceX = data.BalanceX;
        _latestBalanceY = data.BalanceY;
        _boardControlling = data.TemperatureCorrectedWeightKg >= 3;

        if (_boardControlling)
        {
            _boardInput = TiltMazeMath.MapBalanceInput(
                _latestBalanceX,
                _latestBalanceY,
                _centerX,
                _centerY,
                SensitivitySlider.Value,
                deadzonePercent: 0.8);
            SignalStatusText.Text = "ボードで盤面を傾斜中";
            SignalStatusText.Foreground = BrushFrom("#48D7C8");
        }
        else
        {
            _boardInput = default;
            SignalStatusText.Text = "ボード接続済み・乗ると操作開始";
            SignalStatusText.Foreground = BrushFrom("#F0A84A");
        }

        if (_hasBoardButtonState && data.ButtonA && !_boardButtonWasPressed && !_running && _hasCenter)
        {
            StartGame();
        }

        _boardButtonWasPressed = data.ButtonA;
        _hasBoardButtonState = true;
    }

    private void BuildSceneRoot()
    {
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(175, 178, 170)));
        lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.4, -1, -0.5)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(120, 220, 235), new Vector3D(0.8, -0.4, 0.2)));
        SceneViewport.Children.Add(new ModelVisual3D { Content = lights });

        var transforms = new Transform3DGroup();
        transforms.Children.Add(new RotateTransform3D(_pitchRotation));
        transforms.Children.Add(new RotateTransform3D(_rollRotation));
        SceneViewport.Children.Add(new ModelVisual3D
        {
            Content = _boardModels,
            Transform = transforms,
        });
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp(Stopwatch.GetElapsedTime(_lastFrameAt, now).TotalSeconds, 0, 0.05);
        _lastFrameAt = now;

        if (_connected && _lastBoardUpdateAt != 0 &&
            Stopwatch.GetElapsedTime(_lastBoardUpdateAt, now).TotalMilliseconds > 500)
        {
            _boardControlling = false;
            _boardInput = default;
            SignalStatusText.Text = "信号待ち・キーボード操作可";
            SignalStatusText.Foreground = BrushFrom("#F0A84A");
        }

        var tilt = GetEffectiveTilt();
        UpdateBoardTilt(tilt, elapsed);
        UpdateInputReadout(tilt);

        if (_freezeSeconds > 0)
        {
            _freezeSeconds -= elapsed;
            if (_freezeSeconds <= 0 && _running)
            {
                MessagePanel.Visibility = Visibility.Collapsed;
            }
            UpdateHud();
            return;
        }

        if (_running)
        {
            _remainingSeconds = Math.Max(0, _remainingSeconds - elapsed);
            if (_remainingSeconds <= 0)
            {
                EndForTimeout();
            }
            else
            {
                UpdateBalls(tilt, elapsed);
            }
        }

        UpdateHud();
    }

    private TiltMazeInput GetEffectiveTilt()
    {
        var keyboardX = (_right ? 1.0 : 0) - (_left ? 1.0 : 0);
        var keyboardY = (_down ? 1.0 : 0) - (_up ? 1.0 : 0);
        return new TiltMazeInput(
            keyboardX != 0 ? keyboardX : _boardInput.X,
            keyboardY != 0 ? keyboardY : _boardInput.Y);
    }

    private void UpdateBoardTilt(TiltMazeInput tilt, double elapsed)
    {
        var smoothing = 1 - Math.Exp(-elapsed * 8.5);
        _visualTiltX += (tilt.X - _visualTiltX) * smoothing;
        _visualTiltY += (tilt.Y - _visualTiltY) * smoothing;
        _rollRotation.Angle = -_visualTiltX * 10;
        _pitchRotation.Angle = _visualTiltY * 10;
    }

    private void UpdateBalls(TiltMazeInput tilt, double elapsed)
    {
        foreach (var ball in _balls.Where(item => !item.Captured))
        {
            var previous = ball.State;
            var candidate = TiltMazeMath.Step(
                previous,
                tilt.X,
                tilt.Y,
                elapsed,
                -470,
                470,
                -390,
                440);

            foreach (var barrier in _barriers)
            {
                if (TiltMazeMath.CircleIntersectsRectangle(
                    candidate.X,
                    previous.Y,
                    BallRadiusPhysics,
                    barrier.X,
                    barrier.Y,
                    barrier.Width,
                    barrier.Height))
                {
                    candidate = candidate with { X = previous.X, VelocityX = -candidate.VelocityX * 0.36 };
                }

                if (TiltMazeMath.CircleIntersectsRectangle(
                    candidate.X,
                    candidate.Y,
                    BallRadiusPhysics,
                    barrier.X,
                    barrier.Y,
                    barrier.Width,
                    barrier.Height))
                {
                    candidate = candidate with { Y = previous.Y, VelocityY = -candidate.VelocityY * 0.36 };
                }
            }

            ball.State = candidate;
            if (!IsOnPlatform(candidate.X / WorldScale, candidate.Y / WorldScale))
            {
                FallOffPlatform(ball);
                return;
            }

            foreach (var hole in _holes)
            {
                if (TiltMazeMath.IsInsideTarget(
                    candidate.X,
                    candidate.Y,
                    BallRadiusPhysics,
                    hole.X * WorldScale,
                    hole.Z * WorldScale,
                    hole.Radius * WorldScale))
                {
                    CaptureBall(ball);
                    break;
                }
            }
        }

        ResolveBallCollisions();
        RenderBalls();

        if (_balls.Count > 0 && _balls.All(ball => ball.Captured))
        {
            CompleteLevel();
        }
    }

    private void ResolveBallCollisions()
    {
        var active = _balls.Where(ball => !ball.Captured).ToArray();
        for (var firstIndex = 0; firstIndex < active.Length; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < active.Length; secondIndex++)
            {
                var first = active[firstIndex];
                var second = active[secondIndex];
                var deltaX = second.State.X - first.State.X;
                var deltaY = second.State.Y - first.State.Y;
                var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                var minimum = BallRadiusPhysics * 2;
                if (distance <= 0.001 || distance >= minimum)
                {
                    continue;
                }

                var normalX = deltaX / distance;
                var normalY = deltaY / distance;
                var overlap = minimum - distance;
                first.State = first.State with
                {
                    X = first.State.X - normalX * overlap / 2,
                    Y = first.State.Y - normalY * overlap / 2,
                    VelocityX = second.State.VelocityX * 0.82,
                    VelocityY = second.State.VelocityY * 0.82,
                };
                second.State = second.State with
                {
                    X = second.State.X + normalX * overlap / 2,
                    Y = second.State.Y + normalY * overlap / 2,
                    VelocityX = first.State.VelocityX * 0.82,
                    VelocityY = first.State.VelocityY * 0.82,
                };
            }
        }
    }

    private void CaptureBall(GameBall ball)
    {
        ball.Captured = true;
        ball.Model.Transform = new TranslateTransform3D(
            ball.State.X / WorldScale,
            -3,
            ball.State.Y / WorldScale);
        GameStatusText.Text = "球が穴に入りました";
    }

    private void FallOffPlatform(GameBall ball)
    {
        _remainingSeconds = Math.Max(0, _remainingSeconds - 3);
        ball.Reset();
        RenderBall(ball);
        _freezeSeconds = 0.75;
        MessageTitle.Text = "FALL!";
        MessageText.Text = "コース外へ落下　3秒ペナルティ";
        MessagePanel.Visibility = Visibility.Visible;
        GameStatusText.Text = "球がコース外へ落ちました";
    }

    private void CompleteLevel()
    {
        if (_levelIndex >= Levels.Count - 1)
        {
            _running = false;
            _completed = true;
            StartButton.Content = "ゲーム開始";
            MessageTitle.Text = "ALL CLEAR!";
            MessageText.Text = "全5レベルをクリアしました";
            MessagePanel.Visibility = Visibility.Visible;
            GameStatusText.Text = "全レベルクリア！";
            return;
        }

        var nextLevel = _levelIndex + 1;
        LoadLevel(nextLevel);
        _running = true;
        _freezeSeconds = 1.1;
        MessageTitle.Text = "LEVEL CLEAR!";
        MessageText.Text = $"レベル {nextLevel + 1} へ";
        MessagePanel.Visibility = Visibility.Visible;
        GameStatusText.Text = "次のテーブルを準備中";
    }

    private void EndForTimeout()
    {
        _running = false;
        StartButton.Content = "ゲーム開始";
        MessageTitle.Text = "TIME UP";
        MessageText.Text = "球を戻して再挑戦してください";
        MessagePanel.Visibility = Visibility.Visible;
        GameStatusText.Text = "制限時間終了";
    }

    private void LoadLevel(int levelIndex)
    {
        _levelIndex = levelIndex;
        var level = Levels[levelIndex];
        _remainingSeconds = level.Seconds;
        _boardModels.Children.Clear();
        _balls.Clear();
        _holes.Clear();
        _barriers.Clear();

        foreach (var piece in PlatformPieces)
        {
            AddRoundedPlatform(piece);
            AddTileGrid(piece);
        }

        foreach (var barrier in level.Barriers)
        {
            var model = CreateBoxModel(
                barrier.X,
                SurfaceY + 0.24,
                barrier.Z,
                barrier.Width,
                0.46,
                barrier.Depth,
                CreateMaterial(Color.FromRgb(157, 91, 45), Color.FromRgb(255, 195, 120), 45));
            _boardModels.Children.Add(model);
            _barriers.Add(new Rect(
                (barrier.X - barrier.Width / 2) * WorldScale,
                (barrier.Z - barrier.Depth / 2) * WorldScale,
                barrier.Width * WorldScale,
                barrier.Depth * WorldScale));
        }

        foreach (var hole in level.Holes)
        {
            _boardModels.Children.Add(CreateDiscModel(
                hole.X,
                SurfaceY + 0.012,
                hole.Z,
                hole.Radius + 0.09,
                CreateMaterial(Color.FromRgb(115, 74, 45), Colors.Black, 4)));
            _boardModels.Children.Add(CreateDiscModel(
                hole.X,
                SurfaceY + 0.022,
                hole.Z,
                hole.Radius,
                CreateMaterial(Color.FromRgb(20, 18, 16), Color.FromRgb(50, 45, 40), 8)));
            _boardModels.Children.Add(CreateCylinderShellModel(
                hole.X,
                SurfaceY + 0.04,
                hole.Z,
                hole.Radius + 0.08,
                0.65,
                CreateMaterial(Color.FromArgb(72, 175, 235, 244), Color.FromArgb(180, 230, 255, 255), 85)));
            _holes.Add(hole);
        }

        foreach (var ballStart in level.Balls)
        {
            var model = CreateSphereModel(
                BallRadiusWorld,
                CreateMaterial(ballStart.Color, Colors.White, 95));
            var ball = new GameBall(ballStart.X, ballStart.Z, model);
            _balls.Add(ball);
            _boardModels.Children.Add(model);
        }

        RenderBalls();
        UpdateHud();
    }

    private void AddRoundedPlatform(PlatformPiece piece)
    {
        var outline = RoundedRectangleOutline(piece.X, piece.Z, piece.Width, piece.Depth, piece.Radius, 7);
        var topMaterial = CreateMaterial(Color.FromRgb(252, 247, 220), Color.FromRgb(255, 255, 250), 55);
        var sideMaterial = CreateMaterial(Color.FromRgb(143, 82, 43), Color.FromRgb(230, 157, 92), 24);
        _boardModels.Children.Add(CreateTopModel(outline, SurfaceY, topMaterial));
        _boardModels.Children.Add(CreateSideModel(outline, SurfaceY, -0.52, sideMaterial));
    }

    private void AddTileGrid(PlatformPiece piece)
    {
        var lineMaterial = CreateMaterial(Color.FromArgb(150, 105, 103, 92), Colors.White, 5);
        var minX = piece.X - piece.Width / 2 + 0.35;
        var maxX = piece.X + piece.Width / 2 - 0.35;
        var minZ = piece.Z - piece.Depth / 2 + 0.35;
        var maxZ = piece.Z + piece.Depth / 2 - 0.35;
        for (var x = Math.Ceiling(minX); x < maxX; x += 1)
        {
            _boardModels.Children.Add(CreateBoxModel(
                x,
                SurfaceY + 0.014,
                piece.Z,
                0.025,
                0.018,
                Math.Max(0.2, piece.Depth - 0.55),
                lineMaterial));
        }

        for (var z = Math.Ceiling(minZ); z < maxZ; z += 1)
        {
            _boardModels.Children.Add(CreateBoxModel(
                piece.X,
                SurfaceY + 0.014,
                z,
                Math.Max(0.2, piece.Width - 0.55),
                0.018,
                0.025,
                lineMaterial));
        }
    }

    private static IReadOnlyList<Point> RoundedRectangleOutline(
        double centerX,
        double centerZ,
        double width,
        double depth,
        double radius,
        int segmentsPerCorner)
    {
        var points = new List<Point>();
        var halfWidth = width / 2;
        var halfDepth = depth / 2;
        var corners = new[]
        {
            (X: centerX + halfWidth - radius, Z: centerZ + halfDepth - radius, Start: 0.0),
            (X: centerX - halfWidth + radius, Z: centerZ + halfDepth - radius, Start: 90.0),
            (X: centerX - halfWidth + radius, Z: centerZ - halfDepth + radius, Start: 180.0),
            (X: centerX + halfWidth - radius, Z: centerZ - halfDepth + radius, Start: 270.0),
        };

        foreach (var corner in corners)
        {
            for (var segment = 0; segment <= segmentsPerCorner; segment++)
            {
                var angle = (corner.Start + segment * 90.0 / segmentsPerCorner) * Math.PI / 180;
                points.Add(new Point(
                    corner.X + Math.Cos(angle) * radius,
                    corner.Z + Math.Sin(angle) * radius));
            }
        }

        return points;
    }

    private static GeometryModel3D CreateTopModel(IReadOnlyList<Point> outline, double y, Material material)
    {
        var mesh = new MeshGeometry3D();
        var centerX = outline.Average(point => point.X);
        var centerZ = outline.Average(point => point.Y);
        mesh.Positions.Add(new Point3D(centerX, y, centerZ));
        mesh.Normals.Add(new Vector3D(0, 1, 0));
        foreach (var point in outline)
        {
            mesh.Positions.Add(new Point3D(point.X, y, point.Y));
            mesh.Normals.Add(new Vector3D(0, 1, 0));
        }

        for (var index = 0; index < outline.Count; index++)
        {
            mesh.TriangleIndices.Add(0);
            mesh.TriangleIndices.Add(index + 1);
            mesh.TriangleIndices.Add((index + 1) % outline.Count + 1);
        }

        return Model(mesh, material);
    }

    private static GeometryModel3D CreateSideModel(
        IReadOnlyList<Point> outline,
        double topY,
        double bottomY,
        Material material)
    {
        var mesh = new MeshGeometry3D();
        for (var index = 0; index < outline.Count; index++)
        {
            var current = outline[index];
            var next = outline[(index + 1) % outline.Count];
            var start = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(current.X, topY, current.Y));
            mesh.Positions.Add(new Point3D(next.X, topY, next.Y));
            mesh.Positions.Add(new Point3D(next.X, bottomY, next.Y));
            mesh.Positions.Add(new Point3D(current.X, bottomY, current.Y));
            var edge = new Vector3D(next.X - current.X, 0, next.Y - current.Y);
            var normal = new Vector3D(edge.Z, 0, -edge.X);
            normal.Normalize();
            for (var normalIndex = 0; normalIndex < 4; normalIndex++)
            {
                mesh.Normals.Add(normal);
            }

            AddQuad(mesh, start, start + 1, start + 2, start + 3);
        }

        return Model(mesh, material);
    }

    private static GeometryModel3D CreateBoxModel(
        double centerX,
        double centerY,
        double centerZ,
        double width,
        double height,
        double depth,
        Material material)
    {
        var x0 = centerX - width / 2;
        var x1 = centerX + width / 2;
        var y0 = centerY - height / 2;
        var y1 = centerY + height / 2;
        var z0 = centerZ - depth / 2;
        var z1 = centerZ + depth / 2;
        var mesh = new MeshGeometry3D();
        AddFace(mesh, new Point3D(x0, y1, z0), new Point3D(x1, y1, z0), new Point3D(x1, y1, z1), new Point3D(x0, y1, z1), new Vector3D(0, 1, 0));
        AddFace(mesh, new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), new Vector3D(0, -1, 0));
        AddFace(mesh, new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0), new Point3D(x0, y1, z0), new Vector3D(0, 0, -1));
        AddFace(mesh, new Point3D(x1, y0, z1), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), new Vector3D(0, 0, 1));
        AddFace(mesh, new Point3D(x0, y0, z1), new Point3D(x0, y0, z0), new Point3D(x0, y1, z0), new Point3D(x0, y1, z1), new Vector3D(-1, 0, 0));
        AddFace(mesh, new Point3D(x1, y0, z0), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0), new Vector3D(1, 0, 0));
        return Model(mesh, material);
    }

    private static GeometryModel3D CreateDiscModel(
        double centerX,
        double y,
        double centerZ,
        double radius,
        Material material)
    {
        const int segments = 36;
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(new Point3D(centerX, y, centerZ));
        mesh.Normals.Add(new Vector3D(0, 1, 0));
        for (var index = 0; index < segments; index++)
        {
            var angle = index * Math.PI * 2 / segments;
            mesh.Positions.Add(new Point3D(
                centerX + Math.Cos(angle) * radius,
                y,
                centerZ + Math.Sin(angle) * radius));
            mesh.Normals.Add(new Vector3D(0, 1, 0));
        }

        for (var index = 0; index < segments; index++)
        {
            mesh.TriangleIndices.Add(0);
            mesh.TriangleIndices.Add(index + 1);
            mesh.TriangleIndices.Add((index + 1) % segments + 1);
        }

        return Model(mesh, material);
    }

    private static GeometryModel3D CreateSphereModel(double radius, Material material)
    {
        const int latitudeSegments = 16;
        const int longitudeSegments = 24;
        var mesh = new MeshGeometry3D();
        for (var latitude = 0; latitude <= latitudeSegments; latitude++)
        {
            var theta = latitude * Math.PI / latitudeSegments;
            var y = Math.Cos(theta);
            var ring = Math.Sin(theta);
            for (var longitude = 0; longitude <= longitudeSegments; longitude++)
            {
                var phi = longitude * Math.PI * 2 / longitudeSegments;
                var normal = new Vector3D(ring * Math.Cos(phi), y, ring * Math.Sin(phi));
                mesh.Positions.Add(new Point3D(normal.X * radius, normal.Y * radius, normal.Z * radius));
                mesh.Normals.Add(normal);
            }
        }

        for (var latitude = 0; latitude < latitudeSegments; latitude++)
        {
            for (var longitude = 0; longitude < longitudeSegments; longitude++)
            {
                var first = latitude * (longitudeSegments + 1) + longitude;
                var second = first + longitudeSegments + 1;
                mesh.TriangleIndices.Add(first);
                mesh.TriangleIndices.Add(second);
                mesh.TriangleIndices.Add(first + 1);
                mesh.TriangleIndices.Add(first + 1);
                mesh.TriangleIndices.Add(second);
                mesh.TriangleIndices.Add(second + 1);
            }
        }

        return Model(mesh, material);
    }

    private static GeometryModel3D CreateCylinderShellModel(
        double centerX,
        double bottomY,
        double centerZ,
        double radius,
        double height,
        Material material)
    {
        const int segments = 36;
        var mesh = new MeshGeometry3D();
        for (var index = 0; index < segments; index++)
        {
            var currentAngle = index * Math.PI * 2 / segments;
            var nextAngle = (index + 1) * Math.PI * 2 / segments;
            var start = mesh.Positions.Count;
            var currentX = centerX + Math.Cos(currentAngle) * radius;
            var currentZ = centerZ + Math.Sin(currentAngle) * radius;
            var nextX = centerX + Math.Cos(nextAngle) * radius;
            var nextZ = centerZ + Math.Sin(nextAngle) * radius;
            mesh.Positions.Add(new Point3D(currentX, bottomY, currentZ));
            mesh.Positions.Add(new Point3D(nextX, bottomY, nextZ));
            mesh.Positions.Add(new Point3D(nextX, bottomY + height, nextZ));
            mesh.Positions.Add(new Point3D(currentX, bottomY + height, currentZ));
            var normal = new Vector3D(Math.Cos(currentAngle), 0, Math.Sin(currentAngle));
            for (var normalIndex = 0; normalIndex < 4; normalIndex++)
            {
                mesh.Normals.Add(normal);
            }
            AddQuad(mesh, start, start + 1, start + 2, start + 3);
        }

        return Model(mesh, material);
    }

    private static void AddFace(
        MeshGeometry3D mesh,
        Point3D first,
        Point3D second,
        Point3D third,
        Point3D fourth,
        Vector3D normal)
    {
        var start = mesh.Positions.Count;
        mesh.Positions.Add(first);
        mesh.Positions.Add(second);
        mesh.Positions.Add(third);
        mesh.Positions.Add(fourth);
        for (var index = 0; index < 4; index++)
        {
            mesh.Normals.Add(normal);
        }
        AddQuad(mesh, start, start + 1, start + 2, start + 3);
    }

    private static void AddQuad(MeshGeometry3D mesh, int first, int second, int third, int fourth)
    {
        mesh.TriangleIndices.Add(first);
        mesh.TriangleIndices.Add(second);
        mesh.TriangleIndices.Add(third);
        mesh.TriangleIndices.Add(first);
        mesh.TriangleIndices.Add(third);
        mesh.TriangleIndices.Add(fourth);
    }

    private static GeometryModel3D Model(MeshGeometry3D mesh, Material material) =>
        new(mesh, material) { BackMaterial = material };

    private static Material CreateMaterial(Color diffuse, Color specular, double power)
    {
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(new SolidColorBrush(diffuse)));
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(specular), power));
        return group;
    }

    private static bool IsOnPlatform(double x, double z) =>
        PlatformPieces.Any(piece => IsInsideRoundedRectangle(x, z, piece));

    private static bool IsInsideRoundedRectangle(double x, double z, PlatformPiece piece)
    {
        var localX = Math.Abs(x - piece.X);
        var localZ = Math.Abs(z - piece.Z);
        var halfWidth = piece.Width / 2;
        var halfDepth = piece.Depth / 2;
        if (localX > halfWidth || localZ > halfDepth)
        {
            return false;
        }

        if (localX <= halfWidth - piece.Radius || localZ <= halfDepth - piece.Radius)
        {
            return true;
        }

        var cornerX = localX - (halfWidth - piece.Radius);
        var cornerZ = localZ - (halfDepth - piece.Radius);
        return cornerX * cornerX + cornerZ * cornerZ <= piece.Radius * piece.Radius;
    }

    private void RenderBalls()
    {
        foreach (var ball in _balls)
        {
            RenderBall(ball);
        }
    }

    private static void RenderBall(GameBall ball)
    {
        if (ball.Captured)
        {
            return;
        }

        ball.Model.Transform = new TranslateTransform3D(
            ball.State.X / WorldScale,
            SurfaceY + BallRadiusWorld,
            ball.State.Y / WorldScale);
    }

    private void CaptureCenter_Click(object sender, RoutedEventArgs e)
    {
        _centerX = _latestBalanceX;
        _centerY = _latestBalanceY;
        _hasCenter = true;
        StartButton.IsEnabled = true;
        MessageText.Text = "ゲーム開始ボタン または Enter";
        GameStatusText.Text = $"水平姿勢を記録しました X {_centerX:0.0} / Y {_centerY:0.0}";
    }

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SensitivityText.Text = $"{SensitivitySlider.Value:0.0}倍 / 最大傾斜 10°";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _running = false;
            StartButton.Content = "ゲーム開始";
            MessageTitle.Text = "PAUSE";
            MessageText.Text = "ボタンまたは Enter で再開";
            MessagePanel.Visibility = Visibility.Visible;
            GameStatusText.Text = "一時停止中";
            return;
        }

        if (!_hasCenter)
        {
            GameStatusText.Text = "先に現在の姿勢を水平として記録してください";
            return;
        }

        StartGame();
    }

    private void StartGame()
    {
        if (_completed || _remainingSeconds <= 0)
        {
            ResetGame();
        }

        _running = true;
        _freezeSeconds = 0;
        StartButton.Content = "一時停止";
        MessagePanel.Visibility = Visibility.Collapsed;
        GameStatusText.Text = "テーブル傾斜中";
        Focus();
    }

    private void ResetBalls_Click(object sender, RoutedEventArgs e)
    {
        foreach (var ball in _balls)
        {
            ball.Reset();
        }
        RenderBalls();
        GameStatusText.Text = "すべての球を開始位置へ戻しました";
    }

    private void ResetGame_Click(object sender, RoutedEventArgs e) => ResetGame();

    private void ResetGame()
    {
        _running = false;
        _completed = false;
        _freezeSeconds = 0;
        LoadLevel(0);
        StartButton.Content = "ゲーム開始";
        MessageTitle.Text = "3D BALANCE TABLE";
        MessageText.Text = _hasCenter ? "ゲーム開始ボタン または Enter" : "中心姿勢を記録してスタート";
        MessagePanel.Visibility = Visibility.Visible;
        GameStatusText.Text = _hasCenter ? "ゲーム開始できます" : "水平姿勢を記録してください";
    }

    private void UpdateInputReadout(TiltMazeInput tilt)
    {
        TiltXText.Text = $"{tilt.X * 100:+0;-0;0} %";
        TiltYText.Text = $"{tilt.Y * 100:+0;-0;0} %";
    }

    private void UpdateHud()
    {
        LevelText.Text = $"レベル {_levelIndex + 1}";
        TimeText.Text = Math.Ceiling(_remainingSeconds).ToString("0");
        BallsText.Text = $"残り {_balls.Count(ball => !ball.Captured)}";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.A)
        {
            _left = true;
        }
        else if (e.Key is Key.Right or Key.D)
        {
            _right = true;
        }
        else if (e.Key is Key.Up or Key.W)
        {
            _up = true;
        }
        else if (e.Key is Key.Down or Key.S)
        {
            _down = true;
        }
        else if (e.Key == Key.Enter)
        {
            StartButton_Click(sender, e);
        }
        else if (e.Key == Key.R)
        {
            ResetBalls_Click(sender, e);
        }
        else
        {
            return;
        }
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.A)
        {
            _left = false;
        }
        else if (e.Key is Key.Right or Key.D)
        {
            _right = false;
        }
        else if (e.Key is Key.Up or Key.W)
        {
            _up = false;
        }
        else if (e.Key is Key.Down or Key.S)
        {
            _down = false;
        }
        else
        {
            return;
        }
        e.Handled = true;
    }

    private void ClearKeyboardInput()
    {
        _left = false;
        _right = false;
        _up = false;
        _down = false;
    }

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private static readonly IReadOnlyList<PlatformPiece> PlatformPieces =
    [
        new(0.8, 2.0, 7.2, 4.0, 0.78),
        new(-1.25, -1.78, 5.5, 3.15, 0.72),
        new(-0.05, 0.02, 3.4, 1.35, 0.42),
    ];

    private static readonly IReadOnlyList<LevelDefinition> Levels =
    [
        new(
            45,
            [new BallStart(2.5, 2.7, Color.FromRgb(228, 70, 92))],
            [new HoleDefinition(-2.6, -2.1, 0.42)],
            [new BarrierDefinition(0.5, 1.15, 2.2, 0.38)]),
        new(
            50,
            [new BallStart(2.9, 2.6, Color.FromRgb(240, 75, 96)), new BallStart(-2.8, -1.35, Color.FromRgb(235, 239, 235))],
            [new HoleDefinition(-2.65, -2.45, 0.42), new HoleDefinition(2.7, 1.2, 0.42)],
            [new BarrierDefinition(0.25, 1.0, 2.5, 0.4), new BarrierDefinition(-1.35, -2.0, 1.55, 0.36)]),
        new(
            55,
            [new BallStart(2.8, 2.8, Color.FromRgb(235, 70, 92)), new BallStart(-2.7, -1.2, Color.FromRgb(239, 241, 236)), new BallStart(-0.7, -2.4, Color.FromRgb(24, 25, 25))],
            [new HoleDefinition(-2.85, -2.45, 0.4), new HoleDefinition(2.75, 1.05, 0.4), new HoleDefinition(0.7, 3.0, 0.4)],
            [new BarrierDefinition(0.35, 1.0, 2.35, 0.38), new BarrierDefinition(-1.25, -1.95, 1.7, 0.38)]),
        new(
            60,
            [new BallStart(3.0, 2.65, Color.FromRgb(235, 70, 92)), new BallStart(-2.95, -1.15, Color.FromRgb(235, 239, 235)), new BallStart(-0.7, -2.55, Color.FromRgb(20, 21, 22))],
            [new HoleDefinition(-2.7, -2.5, 0.4), new HoleDefinition(2.8, 1.15, 0.4), new HoleDefinition(1.0, 3.0, 0.4)],
            [new BarrierDefinition(0.4, 1.05, 2.2, 0.38), new BarrierDefinition(2.15, 2.25, 0.38, 1.75), new BarrierDefinition(-1.25, -1.95, 1.65, 0.38)]),
        new(
            65,
            [new BallStart(2.65, 2.55, Color.FromRgb(236, 71, 94)), new BallStart(-2.75, -1.15, Color.FromRgb(235, 239, 235)), new BallStart(-0.75, -2.5, Color.FromRgb(18, 19, 19))],
            [new HoleDefinition(-2.7, -2.45, 0.4), new HoleDefinition(2.8, 1.0, 0.4), new HoleDefinition(0.7, 3.0, 0.4)],
            [new BarrierDefinition(0.35, 1.1, 2.25, 0.4), new BarrierDefinition(-1.2, -1.95, 1.65, 0.4), new BarrierDefinition(2.0, 2.4, 0.4, 1.55)]),
    ];

    private sealed class GameBall
    {
        public GameBall(double startX, double startZ, GeometryModel3D model)
        {
            StartX = startX;
            StartZ = startZ;
            Model = model;
            Reset();
        }

        public double StartX { get; }

        public double StartZ { get; }

        public GeometryModel3D Model { get; }

        public TiltBallState State { get; set; }

        public bool Captured { get; set; }

        public void Reset()
        {
            State = new TiltBallState(StartX * WorldScale, StartZ * WorldScale, 0, 0);
            Captured = false;
        }
    }

    private sealed record PlatformPiece(double X, double Z, double Width, double Depth, double Radius);

    private sealed record BallStart(double X, double Z, Color Color);

    private sealed record HoleDefinition(double X, double Z, double Radius);

    private sealed record BarrierDefinition(double X, double Z, double Width, double Depth);

    private sealed record LevelDefinition(
        double Seconds,
        IReadOnlyList<BallStart> Balls,
        IReadOnlyList<HoleDefinition> Holes,
        IReadOnlyList<BarrierDefinition> Barriers);
}
