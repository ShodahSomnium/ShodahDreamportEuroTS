using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// What the game says a truck is, read from its own definitions: the chassis (axle kerb
    /// weights, powered axles, model and variant), the engine (torque, rev limit), the gearbox
    /// (ratios, differential), the tyres, the global physics constants (steering lock, air
    /// resistance) and the interior camera (field of view, head offset, look limits). Everything
    /// here has a source path in the archives; nothing is invented. Truck folders are named
    /// "brand.model" (daf.xf_euro6) and data.sii lists a fallback part per slot as "slot|file".
    /// </summary>
    public sealed class EtsVehicleSpec
    {
        public string TruckId;
        public string Folder;

        // Chassis
        public string ChassisPath;
        public string DetailModel;
        public string ChassisVariant = "chs_4x2";
        public float[] AxleKerbWeights = { 5000f, 2000f };
        public bool[] PoweredAxles = { false, true };
        public float[] ResidualTravel = { 0.1f, 0.09f };

        // Engine
        public string EnginePath;
        public string EngineName;
        public float Torque = 1800f;
        public float RpmLimit = 2200f;

        /// <summary>Pairs of rpm and torque factor, when the engine defines a curve. Empty means flat.</summary>
        public float[] TorqueCurve = new float[0];

        // Transmission
        public string TransmissionPath;
        public string TransmissionName;
        public float DifferentialRatio = 2.64f;
        public float[] ForwardRatios = { 15.86f, 12.33f, 9.57f, 7.44f, 5.87f, 4.57f, 3.47f, 2.7f, 2.1f, 1.63f, 1.29f, 1f };
        public float[] ReverseRatios = { -14.68f, -11.41f };

        // Tyres
        public string FrontTireModel = "/vehicle/truck/upgrade/wheel/tire/front_1.pmd";
        public string RearTireModel = "/vehicle/truck/upgrade/wheel/tire/rear_1.pmd";
        public string TireLook = "gestone";
        public float WetGrip = 0.9f;
        public float RollResistance = 6.5f;

        // Physics (physics.sii)
        public float MaxSteerAngle = 38f;
        public float SteerSensitivityAt0 = 1f;
        public float SteerSensitivityAt100 = 0.4f;
        public float SteerSpeedCoefficient = 4f;
        public float AirResistance = 3f;
        public float BrakeTorqueFactor = 1f;
        public float WheelMass = 85f;
        public float CabinMass = 700f;
        public float FifthWheelPitch = 20f;
        public float FifthWheelRoll = 3f;

        // Interior camera
        public string InteriorCameraUnit;
        public float InteriorFov = 75f;
        public float[] HeadOffset = { 0f, 0f, 0f };
        public float MouseLeftLimit = 210f;
        public float MouseRightLimit = -170f;
        public float MouseUpLimit = 73f;
        public float MouseDownLimit = -65f;
        public float MouseUpDownDefault = -8f;

        public readonly List<string> Notes = new List<string>();

        public float KerbMass
        {
            get
            {
                float total = 0f;
                foreach (float w in AxleKerbWeights) total += w;
                return total;
            }
        }

        public static EtsVehicleSpec Load(EtsFileSystem files, string truckId, out string failure)
        {
            return Load(files, truckId, null, out failure);
        }

        /// <summary>
        /// As above, with the parts a saved vehicle chose in place of data.sii's fallbacks: keys
        /// "chassis", "engine", "transmission", "ftire", "rtire" (EtsTruckAssembly.Parts), each a
        /// definition path. The fallbacks are what the truck folder offers when a save names nothing
        /// (/def/vehicle/truck/daf.xf_euro6/data.sii: "engine|mx11_270.sii", 1,800 Nm); the save is
        /// what the game drives (the DAF preview: engine/mx13_375.sii, 2,500 Nm; the Scania R 6x4's
        /// three-axle chassis, which its fallback 4x2 has not). Null parts is the call above.
        /// </summary>
        public static EtsVehicleSpec Load(EtsFileSystem files, string truckId, IDictionary<string, string> parts, out string failure)
        {
            return Load(files, files.ReadSii, truckId, parts, out failure);
        }

        /// <summary>
        /// As above with the definition reader given (EtsTruckAssembly.Load has the same pair): files.ReadSii on the
        /// main thread, EtsTruckAssembly.UncachedReader(files) on the sliced truck build's worker thread (ReadSii's
        /// cache is a Dictionary the main thread writes). Directory listings and the camera units are read through
        /// files, whose reads are thread safe (EtsWorker's notes).
        /// </summary>
        public static EtsVehicleSpec Load(EtsFileSystem files, Func<string, EtsSiiFile> readSii, string truckId, IDictionary<string, string> parts, out string failure)
        {
            failure = null;
            var spec = new EtsVehicleSpec { TruckId = truckId, Folder = "/def/vehicle/truck/" + truckId };
            EtsSiiFile data = readSii(spec.Folder + "/data.sii");

            if (data == null || data.Units.Count == 0)
            {
                failure = "truck data not found: " + spec.Folder + "/data.sii";
                return null;
            }

            EtsSiiUnit truck = null;
            foreach (EtsSiiUnit unit in data.Units) if (unit.Type == "accessory_truck_data") truck = unit;
            truck = truck ?? data.Units[0];

            var fallbacks = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string entry in truck.GetArray("fallback"))
            {
                int bar = entry.IndexOf('|');
                if (bar > 0) fallbacks[entry.Substring(0, bar)] = spec.Folder + "/" + entry.Substring(0, bar) + "/" + entry.Substring(bar + 1);
            }

            if (parts != null)
            {
                var taken = new List<string>();

                foreach (KeyValuePair<string, string> part in parts)
                {
                    string slot = part.Key == "ftire" ? "f_tire" : part.Key == "rtire" ? "r_tire" : part.Key;

                    if ((slot == "chassis" || slot == "engine" || slot == "transmission" || slot == "f_tire" || slot == "r_tire") && !string.IsNullOrEmpty(part.Value))
                    {
                        fallbacks[slot] = part.Value;
                        taken.Add(slot + " " + part.Value.Substring(part.Value.LastIndexOf('/') + 1));
                    }
                }

                taken.Sort(StringComparer.Ordinal);
                spec.Notes.Add("parts from the save: " + string.Join(", ", taken.ToArray()));
            }

            // Chassis
            if (fallbacks.TryGetValue("chassis", out spec.ChassisPath))
            {
                EtsSiiUnit chassis = FirstUnit(readSii(spec.ChassisPath), "accessory_chassis_data");

                if (chassis != null)
                {
                    spec.DetailModel = chassis.GetString("detail_model", spec.DetailModel);
                    spec.ChassisVariant = chassis.GetString("variant", spec.ChassisVariant);
                    float[] weights = Floats(chassis.GetArray("kerb_weight"));
                    if (weights.Length > 0) spec.AxleKerbWeights = weights;
                    List<string> powered = chassis.GetArray("powered_axle");
                    if (powered.Count > 0) { spec.PoweredAxles = new bool[powered.Count]; for (int i = 0; i < powered.Count; i++) spec.PoweredAxles[i] = powered[i].Trim() == "true"; }
                    float[] travel = Floats(chassis.GetArray("residual_travel"));
                    if (travel.Length > 0) spec.ResidualTravel = travel;
                }
                else spec.Notes.Add("chassis unit not found in " + spec.ChassisPath);
            }

            // Engine
            if (fallbacks.TryGetValue("engine", out spec.EnginePath))
            {
                EtsSiiUnit engine = FirstUnit(readSii(spec.EnginePath), "accessory_engine_data");

                if (engine != null)
                {
                    spec.EngineName = engine.GetString("name", spec.EnginePath);
                    spec.Torque = engine.GetFloat("torque", spec.Torque);
                    spec.RpmLimit = engine.GetFloat("rpm_limit", spec.RpmLimit);
                    List<string> curve = engine.GetArray("torque_curve");
                    var pairs = new List<float>();
                    foreach (string point in curve) { float[] p = EtsSiiValue.ParseFloats(point); if (p.Length >= 2) { pairs.Add(p[0]); pairs.Add(p[1]); } }
                    spec.TorqueCurve = pairs.ToArray();
                }
                else spec.Notes.Add("engine unit not found in " + spec.EnginePath);
            }

            // Transmission
            if (fallbacks.TryGetValue("transmission", out spec.TransmissionPath))
            {
                EtsSiiUnit gearbox = FirstUnit(readSii(spec.TransmissionPath), "accessory_transmission_data");

                if (gearbox != null)
                {
                    spec.TransmissionName = gearbox.GetString("name", spec.TransmissionPath);
                    spec.DifferentialRatio = gearbox.GetFloat("differential_ratio", spec.DifferentialRatio);
                    float[] forward = Floats(gearbox.GetArray("ratios_forward"));
                    if (forward.Length > 0) spec.ForwardRatios = forward;
                    float[] reverse = Floats(gearbox.GetArray("ratios_reverse"));
                    if (reverse.Length > 0) spec.ReverseRatios = reverse;
                }
                else spec.Notes.Add("transmission unit not found in " + spec.TransmissionPath);
            }

            // Tyres
            if (fallbacks.TryGetValue("f_tire", out string frontTire))
            {
                EtsSiiUnit tire = FirstUnit(readSii(frontTire), "accessory_wheel_data");
                if (tire != null) { spec.FrontTireModel = tire.GetString("model", spec.FrontTireModel); spec.TireLook = tire.GetString("look", spec.TireLook); spec.WetGrip = tire.GetFloat("wet_grip", spec.WetGrip); spec.RollResistance = tire.GetFloat("roll_resistance", spec.RollResistance); }
            }

            if (fallbacks.TryGetValue("r_tire", out string rearTire))
            {
                EtsSiiUnit tire = FirstUnit(readSii(rearTire), "accessory_wheel_data");
                if (tire != null) spec.RearTireModel = tire.GetString("model", spec.RearTireModel);
            }

            // Global physics
            EtsSiiUnit physics = FirstUnit(readSii("/def/vehicle/physics.sii"), "physics_data");

            if (physics != null)
            {
                spec.MaxSteerAngle = physics.GetFloat("max_visual_rotation", spec.MaxSteerAngle);
                spec.SteerSensitivityAt0 = physics.GetFloat("steering_sensitivity_multiplier_0kmh", spec.SteerSensitivityAt0);
                spec.SteerSensitivityAt100 = physics.GetFloat("steering_sensitivity_multiplier_100kmh", spec.SteerSensitivityAt100);
                spec.SteerSpeedCoefficient = physics.GetFloat("steering_speed_coef", spec.SteerSpeedCoefficient);
                spec.AirResistance = physics.GetFloat("air_resistance", spec.AirResistance);
                spec.BrakeTorqueFactor = physics.GetFloat("brake_torque_factor", spec.BrakeTorqueFactor);
                spec.WheelMass = physics.GetFloat("wheel_mass", spec.WheelMass);
                spec.CabinMass = physics.GetFloat("cabin_mass", spec.CabinMass);
                spec.FifthWheelPitch = physics.GetFloat("fifth_wheel_pitch", spec.FifthWheelPitch);
                spec.FifthWheelRoll = physics.GetFloat("fifth_wheel_roll", spec.FifthWheelRoll);
            }
            else spec.Notes.Add("physics.sii not found");

            // Interior camera: the unit named by data.sii lives in one of the /def/camera/units files.
            spec.InteriorCameraUnit = truck.GetString("interior_camera", null);

            if (!string.IsNullOrEmpty(spec.InteriorCameraUnit) && files.TryList("/def/camera/units", out List<string> cameraFiles, out _))
            {
                EtsSiiUnit camera = null;

                foreach (string file in cameraFiles)
                {
                    if (!file.StartsWith("interior", StringComparison.Ordinal) || camera != null) continue;
                    string text = files.ReadText("/def/camera/units/" + file);
                    if (text == null || !text.Contains(spec.InteriorCameraUnit)) continue;

                    try
                    {
                        EtsSiiFile parsed = EtsSii.Parse("SiiNunit\n{\n" + text + "\n}\n", "/def/camera/units/" + file, files.ReadText);
                        camera = parsed.Find(spec.InteriorCameraUnit);
                    }
                    catch (Exception e)
                    {
                        spec.Notes.Add("camera unit " + file + ": " + e.Message);
                    }
                }

                if (camera != null)
                {
                    spec.InteriorFov = camera.GetFloat("camera_fov", spec.InteriorFov);
                    float[] head = camera.GetFloats("head_offset");
                    if (head.Length >= 3) spec.HeadOffset = head;
                    spec.MouseLeftLimit = camera.GetFloat("mouse_left_limit", spec.MouseLeftLimit);
                    spec.MouseRightLimit = camera.GetFloat("mouse_right_limit", spec.MouseRightLimit);
                    spec.MouseUpLimit = camera.GetFloat("mouse_up_limit", spec.MouseUpLimit);
                    spec.MouseDownLimit = camera.GetFloat("mouse_down_limit", spec.MouseDownLimit);
                    spec.MouseUpDownDefault = camera.GetFloat("mouse_up_down_default", spec.MouseUpDownDefault);
                }
                else spec.Notes.Add("interior camera unit not found: " + spec.InteriorCameraUnit);
            }

            return spec;
        }

        /// <summary>Engine torque at an rpm: the curve when the engine has one, else the game's
        /// flat plateau between 900 and 1400 rpm falling to two thirds at the limit.</summary>
        public float TorqueAt(float rpm)
        {
            if (TorqueCurve.Length >= 4)
            {
                if (rpm <= TorqueCurve[0]) return Torque * TorqueCurve[1];

                for (int i = 2; i < TorqueCurve.Length; i += 2)
                {
                    if (rpm <= TorqueCurve[i])
                    {
                        float t = (rpm - TorqueCurve[i - 2]) / Math.Max(1f, TorqueCurve[i] - TorqueCurve[i - 2]);
                        return Torque * (TorqueCurve[i - 1] + (TorqueCurve[i + 1] - TorqueCurve[i - 1]) * t);
                    }
                }

                return Torque * TorqueCurve[TorqueCurve.Length - 1];
            }

            if (rpm < 600f) return Torque * 0.5f;
            if (rpm < 900f) return Torque * (0.5f + 0.5f * (rpm - 600f) / 300f);
            if (rpm <= 1400f) return Torque;
            if (rpm < RpmLimit) return Torque * (1f - 0.35f * (rpm - 1400f) / Math.Max(1f, RpmLimit - 1400f));
            return 0f;
        }

        private static EtsSiiUnit FirstUnit(EtsSiiFile file, string type)
        {
            if (file == null) return null;
            foreach (EtsSiiUnit unit in file.Units) if (unit.Type == type) return unit;
            return file.Units.Count > 0 ? file.Units[0] : null;
        }

        private static float[] Floats(List<string> values)
        {
            var result = new List<float>();
            foreach (string v in values) { float[] p = EtsSiiValue.ParseFloats(v); if (p.Length > 0) result.Add(p[0]); }
            return result.ToArray();
        }

        public override string ToString()
        {
            return TruckId + ": " + KerbMass.ToString("0") + " kg on " + AxleKerbWeights.Length + " axles, " + EngineName + " " + Torque + " Nm to " + RpmLimit + " rpm, " +
                   TransmissionName + " " + ForwardRatios.Length + " gears x " + DifferentialRatio + ", steer " + MaxSteerAngle + " deg, drag " + AirResistance;
        }
    }
}
