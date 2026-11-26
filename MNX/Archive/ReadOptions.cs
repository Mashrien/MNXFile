namespace MNX;

public sealed partial class Configuration {
    public const int MaximumReadWorkers = 4;
    private int _readWorkers = DefaultReadWorkers();

    /// <summary>Maximum simultaneous chunk readers per archive operation. One uses synchronous I/O.</summary>
    public int ReadWorkers {
        get => _readWorkers;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaximumReadWorkers);
            _readWorkers = value;
            }
        }

    private static int DefaultReadWorkers() {
        string? configured = Environment.GetEnvironmentVariable("MNX_READ_WORKERS");
        if (int.TryParse(configured, out int workers) && workers >= 1 && workers <= MaximumReadWorkers) {
            return workers;
            }
        return 2;
        }
    }
