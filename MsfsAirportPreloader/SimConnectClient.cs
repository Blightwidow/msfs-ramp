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
    /// Thin SimConnect wrapper: opens a connection, subscribes to aircraft lat/lon/ground-speed
    /// once per second, and raises PositionUpdated. Runs its own message-pump thread so callers
    /// just consume position events.
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
        private readonly EventWaitHandle _messageEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        private SimConnect _simConnect;
        private Thread _pumpThread;
        private volatile bool _running;

        public event Action<AircraftPosition> PositionUpdated;
        public bool IsConnected => _simConnect != null;

        public SimConnectClient(Action<string> log) => _log = log;

        public bool TryConnect()
        {
            try
            {
                _simConnect = new SimConnect(
                    "MsfsAirportPreloader",
                    IntPtr.Zero,
                    WM_USER_SIMCONNECT,
                    _messageEvent,
                    0);

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

                _running = true;
                _pumpThread = new Thread(MessagePump) { IsBackground = true, Name = "SimConnectPump" };
                _pumpThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"SimConnect connect failed: {ex.Message}");
                _simConnect = null;
                return false;
            }
        }

        private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            _log?.Invoke($"SimConnect connected to: {data.szApplicationName}");
            // One update per second is plenty; the aircraft can't cross the 60->25 NM
            // band faster than the prefetch thread can warm files.
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
            _log?.Invoke("SimConnect: sim closed.");
            _running = false;
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
            while (_running)
            {
                if (_messageEvent.WaitOne(1000))
                {
                    try
                    {
                        _simConnect?.ReceiveMessage();
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"SimConnect receive error: {ex.Message}");
                        _running = false;
                    }
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _messageEvent.Set();
            _pumpThread?.Join(2000);
            try
            {
                _simConnect?.Dispose();
            }
            catch
            {
                // ignore
            }

            _simConnect = null;
            _messageEvent.Dispose();
        }
    }
}
