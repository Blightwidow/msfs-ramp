using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.FlightSimulator.SimConnect;

namespace MsfsAirportPreloader
{
    internal readonly struct AircraftPosition
    {
        public AircraftPosition(double latitude, double longitude, double groundSpeedKnots)
        {
            Latitude = latitude;
            Longitude = longitude;
            GroundSpeedKnots = groundSpeedKnots;
        }

        public double Latitude { get; }
        public double Longitude { get; }
        public double GroundSpeedKnots { get; }
    }

    /// <summary>
    /// SimConnect connection with automatic recovery: call EnsureConnected() on a timer;
    /// it (re)establishes the link whenever the sim is available and tears it down cleanly
    /// when the sim quits, so the app can run indefinitely with MSFS off and reconnect on
    /// the next launch. Raises PositionUpdated (~1 Hz) and ConnectionChanged.
    /// </summary>
    internal sealed class SimConnectClient : IDisposable
    {
        private const int WM_USER_SIMCONNECT = 0x0402;

        private enum Definition { AircraftState }
        private enum Request { AircraftState }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct AircraftStateStruct
        {
            public double Latitude;
            public double Longitude;
            public double GroundSpeedKnots;
        }

        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private EventWaitHandle _messageEvent;
        private SimConnect _simConnect;
        private Thread _pumpThread;
        private volatile bool _connected;
        private volatile bool _pumpRunning;

        public event Action<AircraftPosition> PositionUpdated;
        public event Action<bool> ConnectionChanged;

        public bool IsConnected => _connected;

        public SimConnectClient(Action<string> log) => _log = log;

        /// <summary>Idempotent: connects if not connected, no-op if already connected. Safe to poll.</summary>
        public void EnsureConnected()
        {
            lock (_gate)
            {
                if (_simConnect != null)
                {
                    return; // already connected (or connecting); teardown clears this
                }

                try
                {
                    _messageEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                    _simConnect = new SimConnect("MsfsAirportPreloader", IntPtr.Zero, WM_USER_SIMCONNECT, _messageEvent, 0);

                    _simConnect.OnRecvOpen += OnRecvOpen;
                    _simConnect.OnRecvQuit += OnRecvQuit;
                    _simConnect.OnRecvException += OnRecvException;
                    _simConnect.OnRecvSimobjectData += OnRecvSimobjectData;

                    _simConnect.AddToDataDefinition(Definition.AircraftState, "PLANE LATITUDE", "degrees",
                        SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                    _simConnect.AddToDataDefinition(Definition.AircraftState, "PLANE LONGITUDE", "degrees",
                        SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                    _simConnect.AddToDataDefinition(Definition.AircraftState, "GROUND VELOCITY", "knots",
                        SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                    _simConnect.RegisterDataDefineStruct<AircraftStateStruct>(Definition.AircraftState);

                    _pumpRunning = true;
                    _pumpThread = new Thread(MessagePump) { IsBackground = true, Name = "SimConnectPump" };
                    _pumpThread.Start();
                }
                catch (Exception ex)
                {
                    // Sim not running yet — expected. Clean up and try again next tick.
                    _log?.Invoke($"SimConnect not available: {ex.Message}");
                    TeardownLocked(raiseEvent: false);
                }
            }
        }

        private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            _log?.Invoke($"SimConnect connected to: {data.szApplicationName}");
            _connected = true;
            ConnectionChanged?.Invoke(true);

            _simConnect.RequestDataOnSimObject(
                Request.AircraftState,
                Definition.AircraftState,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SECOND,
                SIMCONNECT_DATA_REQUEST_FLAG.CHANGED,
                0, 0, 0);
        }

        private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            _log?.Invoke("SimConnect: sim closed — will reconnect when it returns.");
            RequestTeardown();
        }

        private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            _log?.Invoke($"SimConnect exception: {(SIMCONNECT_EXCEPTION)data.dwException}");
        }

        private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if (data.dwRequestID != (uint)Request.AircraftState)
            {
                return;
            }

            var state = (AircraftStateStruct)data.dwData[0];
            PositionUpdated?.Invoke(new AircraftPosition(state.Latitude, state.Longitude, state.GroundSpeedKnots));
        }

        private void MessagePump()
        {
            while (_pumpRunning)
            {
                try
                {
                    if (_messageEvent.WaitOne(1000))
                    {
                        _simConnect?.ReceiveMessage();
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"SimConnect receive error: {ex.Message}");
                    RequestTeardown();
                    return;
                }
            }
        }

        /// <summary>Ask for teardown from a callback/pump thread without deadlocking on _gate.</summary>
        private void RequestTeardown()
        {
            // The pump thread may be inside ReceiveMessage; do the teardown on a short-lived
            // thread so we never join the pump from within itself.
            _pumpRunning = false;
            var cleanup = new Thread(() =>
            {
                lock (_gate)
                {
                    TeardownLocked(raiseEvent: true);
                }
            }) { IsBackground = true };
            cleanup.Start();
        }

        private void TeardownLocked(bool raiseEvent)
        {
            bool wasConnected = _connected;
            _connected = false;
            _pumpRunning = false;

            if (_pumpThread != null && _pumpThread != Thread.CurrentThread)
            {
                _pumpThread.Join(2000);
            }

            _pumpThread = null;

            try { _simConnect?.Dispose(); } catch { /* ignore */ }
            _simConnect = null;

            try { _messageEvent?.Dispose(); } catch { /* ignore */ }
            _messageEvent = null;

            if (raiseEvent && wasConnected)
            {
                ConnectionChanged?.Invoke(false);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                TeardownLocked(raiseEvent: false);
            }
        }
    }
}
