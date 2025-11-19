using hyper.Command;
using hyper.commands;
using hyper.config;
using hyper.Database.DAO;
using hyper.Helper;
using hyper.Inputs;
using hyper.Models;
using hyper.Output;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Utils;
using ZWave.BasicApplication.Devices;
using ZWave.CommandClasses;

namespace hyper
{
    public class InteractiveCommand : BaseCommand
    {
        private const string RETRYDELAYSFORBASIC_DEFAULT = "";
        private const string RETRYDELAYSFORBASIC_KEY = "retryDelaysForBasic";
        private const string RAZBERRY_PORT = "/dev/ttyAMA0";

        private ICommand currentCommand = null;

        private bool blockExit = false;
        private string args;
        private InputManager inputManager;
        private EventDAO eventDao = new EventDAO();
        private string port;
        private bool simulationMode;
        private static readonly string oneTo255Regex = @"\b(?:[1-9]|[1-8][0-9]|9[0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5])\b";
        private static readonly string zeroTo255Regex = @"\b(?:[0-9]|[1-8][0-9]|9[0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5])\b";
        private int[] retryDelaysForBasic = new int[0];
        //the request number of binary/basic for each device
        //retries must check it to avoid setting an obsolete value
        //it is not thread safe, but the commands are always executed in the same thread
        IDictionary<byte, byte> basicRequestNumbers = new Dictionary<byte, byte>();

        public InteractiveCommand(string args, InputManager inputManager, string port)
        {
            this.args = args;
            this.inputManager = inputManager;
            this.port = port;
            this.simulationMode = (port == StartArguments.SimulatePort);
        }

        public bool Active { get; private set; } = false;

        private void CancelCommand()
        {
            //comes from TCPInput and is useful for non interactive usage
            Common.logger.Info("current command should already be stopped, nothing to do");
        }

        private void CancelHandler(object evtSender, ConsoleCancelEventArgs evtArgs)
        {
            Common.logger.Debug(Util.ObjToJson(evtSender));

            if (evtArgs != null)
            {
                evtArgs.Cancel = true;
            }
            if (blockExit)
            {
                Common.logger.Info("Cannot abort application now!\nPlease wait for operation to finish.");
                return;
            }

            if (currentCommand != null)
            {
                Common.logger.Info("Stopping current command!");
                currentCommand.Stop();
            }
            else if (evtSender != null && evtSender is IInput)
            {
                //TCPInput sets evtSender for the cancel command
                Common.logger.Info("No current command, but cancel command recognized, nothing to do");
            }
            else if (evtSender == null)
            {
                Common.logger.Info("No current command, stopping application");
                if (evtArgs != null)
                {
                    evtArgs.Cancel = false;
                }
                Environment.Exit(0);
                return;
            }
        }

        public override bool Start()
        {
            ReadProgramConfig();

            Console.CancelKeyPress += new ConsoleCancelEventHandler(CancelHandler);
            inputManager.CancelKeyPress += new ConsoleCancelEventHandler(CancelHandler);
            // InputManager.AddCancelEventHandler(CancelHandler);

            var batteryRegex = new Regex(@$"^battery\s*({oneTo255Regex})");
            var pingRegex = new Regex(@$"^ping\s*({oneTo255Regex})");
            var wakeUpRegex = new Regex(@$"^wakeup\s*({oneTo255Regex})\s*([0-9]+)?");
            var wakeUpCapRegex = new Regex(@$"^wakeupcap\s*({oneTo255Regex})");
            var basicRegex = new Regex(@$"^(basic|binary)\s*({oneTo255Regex})\s*(false|true)");
            var retryRegex = new Regex(@$"^(basic|binary)retry\s*({oneTo255Regex})\s*(false|true)\s*(\d+)?\s*(\d+)?");
            var basicGetRegex = new Regex(@$"^(basic|binary)\s*({oneTo255Regex})");
            var listenRegex = new Regex(@$"^listen\s*(stop|start|filter\s*({oneTo255Regex}))");
            var forceRemoveRegex = new Regex(@$"^remove\s*({oneTo255Regex})");
            var debugRegex = new Regex(@"^debug\s*(false|true)");
            var lastEventsRegex = new Regex(@$"^show\s*({zeroTo255Regex})\s*([0-9]+)?\s*([a-zA-Z_]+)?"); //nicht auf 255 einschränken!
            var multiRegex = new Regex(@$"^multi\s*({oneTo255Regex})\s*({zeroTo255Regex})\s*(false|true)");
            var rtr_setpointRegex = new Regex(@$"^rtr_setpoint\s*({oneTo255Regex})\s*(\d+\.?\d*)?");
            var rtr_modeRegex = new Regex(@$"^rtr_mode\s*({oneTo255Regex})\s*(\d+)?");
            var rtr_operatingStateRegex = new Regex(@$"^rtr_operating_state\s*({oneTo255Regex})");
            var deleteOldeventsRegex = new Regex($@"^deleteoldevents\s*(\d+)*\s*(\d+)*");

            Active = true;
            bool oneShot = args.Length > 0;

            Common.logger.Info("-----------");

            const string HelpHint = "use 'help' for a list of available commands";
            Common.logger.Info(oneShot ? "Oneshot mode" : "Interaction mode - " + HelpHint);

            Common.logger.Info("-----------");

            ListenCommand listenComand = new ListenCommand(Program.controller, Program.configList);
            QueueCommand queueCommand = new QueueCommand(Program.controller, Program.configList, inputManager);

            if (!simulationMode)
            {
                Thread InstanceCallerListen = new Thread(
                    new ThreadStart(() => listenComand.Start()));

                InstanceCallerListen.Start();

                Thread InstanceCallerQueue = new Thread(
                    new ThreadStart(() => queueCommand.Start()));

                InstanceCallerQueue.Start();
            }

            do
            {
                Common.logger.Info("choose your destiny girl!");

                var input = "";
                if (oneShot)
                {
                    input = args;
                }
                else
                {
                    input = inputManager.ReadAny();
                }
                if (input == null)
                {
                    return false;
                }
                Common.logger.Info("Command: {0}", input);
                switch (input.Trim().ToLower())
                {
                    case "cancel":
                        CancelCommand();
                        break;
                    case "help":
                        ShowHelp();
                        break;
                    case "homeid":
                        LogHomeId();
                        break;
                    case "profiles":
                        ShowProfiles();
                        break;
                    case "test":
                        {
                            foreach (var n in Program.controller.IncludedNodes)
                            {
                                var node = new ZWave.Devices.NodeTag(n);

                                var infoRes = Program.controller.GetProtocolInfo(node);
                                Program.controller.Network.SetNodeInfo(node, infoRes.NodeInfo);

                                Console.WriteLine(n + " : " + Program.controller.Network.IsListening(node));
                            }

                            break;
                        }
                    case "reset!":
                        {
                            blockExit = true;
                            LogHomeId();
                            Common.logger.Info("Resetting controller...");
                            Program.controller.SetDefault();
                            Program.controller.SerialApiGetInitData();
                            Program.controller.MemoryGetId(); //refresh the HomeId
                            LogHomeId();
                            Common.logger.Info("Done!");

                            blockExit = false;
                            break;
                        }
                    case var includeVal when IncludeCommand.IsMatch(includeVal):
                        {
                            string profile = IncludeCommand.GetProfile(includeVal);
                            currentCommand = new IncludeCommand(Program.controller, Program.configList, profile);
                            break;
                        }
                    case "included":
                        LogIncludedNodes();
                        break;
                    case "exclude":
                        {
                            currentCommand = new ExcludeCommand(Program.controller);
                            break;
                        }
                    case "backup":
                        {
                            Backup(false);
                            break;
                        }
                    case "backup 256k":
                        {
                            Backup(true);
                            break;
                        }
                    case var restoreCmd when RestoreCommand.IsMatch(restoreCmd):
                        {
                            if (simulationMode)
                            {
                                Common.logger.Info("Simulation Mode, ignoring command");
                                break;
                            }
                            var oldHomeId = LogHomeId();
                            if (port == RAZBERRY_PORT)
                            {
                                Common.logger.Error($"restore not possible with port {port}");
                                break;
                            }
                            blockExit = true;
                            try
                            {
                                string filename = RestoreCommand.GetFileName(restoreCmd);
                                byte[] read = File.ReadAllBytes(filename);
                                var result = Common.WriteNVRam(Program.controller, read);
                                Common.logger.Info("Result is {0}", result);
                                if (result)
                                {
                                    Program.controller.SerialApiGetInitData();
                                    Program.controller.MemoryGetId(); //refresh the HomeId
                                    var newHomeId = LogHomeId();
                                    var msg = "Eeprom restore finished.";
                                    if (newHomeId != oldHomeId)
                                    {
                                        msg += " Replug Z-Stick and restart hyper";
                                    }
                                    Common.logger.Info(msg);
                                }
                            }
                            catch (FileNotFoundException ex)
                            {
                                Common.logger.Error(ex.Message);
                            }

                            blockExit = false;
                            break;
                        }
                    case "reload":
                        {
                            Common.logger.Info("Reloading conifg!");
                            Program.configList = Common.ParseConfig("config.yaml");
                            listenComand.UpdateConfig(Program.configList);
                            Program.programConfig.LoadFromFile();
                            ReadProgramConfig();
                            break;
                        }
                    case var batteryVal when batteryRegex.IsMatch(batteryVal):
                        {
                            var match = batteryRegex.Match(batteryVal);
                            var nodeId = byte.Parse(match.Groups[1].Value);
                            blockExit = true;
                            Common.RequestBatteryReport(Program.controller, nodeId);
                            blockExit = false;
                            break;
                        }
                    case var deleteOldEventsVal when deleteOldeventsRegex.IsMatch(deleteOldEventsVal):
                        {
                            var match = deleteOldeventsRegex.Match(deleteOldEventsVal);
                            int maxAge = 180;
                            int daysAtOnce = 10;
                            if (!String.IsNullOrEmpty(match.Groups[1].Value))
                            {
                                int.TryParse(match.Groups[1].Value, out maxAge);
                            }
                            if (!String.IsNullOrEmpty(match.Groups[2].Value))
                            {
                                int.TryParse(match.Groups[2].Value, out daysAtOnce);
                            }
                            Common.logger.Info("Starting DeleteOldEvents asynchronously, the command loop is not blocked");
                            Task.Run(() => Common.DeleteOldEvents(maxAge, daysAtOnce));
                            break;
                        }
                    case var listenVal when listenRegex.IsMatch(listenVal):
                        {
                            var val = listenRegex.Match(listenVal).Groups[1].Value;
                            if (val == "start" || val == "stop")
                            {
                                listenComand.Active = val == "start";
                            }
                            else if (val.StartsWith("filter"))
                            {
                                var nodeId = Byte.Parse(listenRegex.Match(listenVal).Groups[2].Value);
                                listenComand.Filter = nodeId;
                                //   Console.WriteLine("FILTER {0}", nodeId);
                            }
                            break;
                        }
                    case var multiVal when multiRegex.IsMatch(multiVal):
                        {
                            if (simulationMode)
                            {
                                Common.logger.Info("Simulation Mode, ignoring command");
                                break;
                            }
                            var match = multiRegex.Match(multiVal);
                            var nodeId = byte.Parse(match.Groups[1].Value);
                            var endpoint = byte.Parse(match.Groups[2].Value);
                            var value = bool.Parse(match.Groups[3].Value);
                            Common.SetMulti(Program.controller, nodeId, endpoint, value);
                            break;
                        }
                    case var queueVal when QueueCommand.IsMatch(queueVal):
                        {
                            var nodeIds = QueueCommand.GetNodeIds(queueVal);
                            var command = QueueCommand.GetCommand(queueVal);
                            var param = QueueCommand.GetParameter(queueVal);
                            foreach (var nodeId in nodeIds)
                            {
                                var fullCommand = String.Format($"{command} {nodeId} {param}!");
                                Common.logger.Info($"node: {nodeId} - command: {fullCommand}");
                                queueCommand.AddToMap(nodeId, fullCommand);
                            }
                            break;
                        }
                    case var eventsVal when lastEventsRegex.IsMatch(eventsVal):
                        {
                            var match = lastEventsRegex.Match(eventsVal);
                            var nodeId = byte.Parse(match.Groups[1].Value);

                            var count = match.Groups[2].Value;
                            var command = match.Groups[3].Value;

                            if (count.IsNullOrEmpty())
                            {
                                count = "200";
                            }

                            Common.logger.Info($"node: {nodeId} - count: {(count.IsNullOrEmpty() ? "all" : count)} - command: {(command.IsNullOrEmpty() ? "all" : command)}");
                            EventFilter filter = new EventFilter();
                            if (nodeId != 0)
                            {
                                filter.NodeId = nodeId;
                            }
                            if (!count.IsNullOrEmpty())
                            {
                                filter.Count = int.Parse(count);
                            }
                            if (!command.IsNullOrEmpty())
                            {
                                filter.Command = command;
                            }
                            List<Event> events = eventDao.GetByFilter(filter);
                            foreach (var evt in events)
                            {
                                Common.logger.Info(evt);
                            }
                            break;
                        }
                    case var debugVal when debugRegex.IsMatch(debugVal):
                        {
                            var val = debugRegex.Match(debugVal).Groups[1].Value;
                            var debug = bool.Parse(val);
                            listenComand.Debug = debug;
                            LoggingSetupHelper.SetDebugLevel(debug);
                            break;
                        }
                    case var removeVal when forceRemoveRegex.IsMatch(removeVal):
                        {
                            var val = forceRemoveRegex.Match(removeVal).Groups[1].Value;
                            var nodeId = byte.Parse(val);
                            currentCommand = new ForceRemoveCommand(Program.controller, nodeId);
                            break;
                        }
                    //case var testVal when testRegex.IsMatch(testVal):
                    //    {
                    //        var val = testRegex.Match(testVal).Groups[1].Value;
                    //        var nodeId = Byte.Parse(val);

                    //        byte[] bytes = new byte[256];
                    //        byte[] numArray = File.ReadAllBytes(@"C:\Users\james\Desktop\tmp\MultiSensor 6_OTA_EU_A_V1_13.exe");
                    //        int length = (int)numArray[numArray.Length - 4] << 24 | (int)numArray[numArray.Length - 3] << 16 | (int)numArray[numArray.Length - 2] << 8 | (int)numArray[numArray.Length - 1];
                    //        byte[] flashData = new byte[length];
                    //        Array.Copy((Array)numArray, numArray.Length - length - 4 - 4 - 256, (Array)flashData, 0, length);
                    //        Array.Copy((Array)numArray, numArray.Length - 256 - 4 - 4, (Array)bytes, 0, 256);

                    //        var cmd = new COMMAND_CLASS_FIRMWARE_UPDATE_MD_V2.FIRMWARE_UPDATE_MD_REQUEST_GET();
                    //        cmd.manufacturerId = new byte[] { 0, 0x86 };
                    //        cmd.firmwareId = new byte[] { 0, 0 };
                    //        cmd.checksum = Tools.CalculateCrc16Array(flashData);
                    //        Program.controller.SendData(nodeId, cmd, Common.txOptions);

                    //        break;
                    //    }
                    case var pingVal when pingRegex.IsMatch(pingVal):
                        {
                            blockExit = true;
                            var val = pingRegex.Match(pingVal).Groups[1].Value;
                            var nodeId = byte.Parse(val);
                            Common.logger.Info("Pinging node {0}...", nodeId);
                            var reachable = Common.CheckReachable(Program.controller, nodeId);
                            Common.logger.Info("node {0} is{1}reachable!", nodeId, reachable ? " " : " NOT ");
                            blockExit = false;
                            // currentCommand = new PingCommand(Program.controller, nodeId);
                            break;
                        }
                    case var basicSetVal when basicRegex.IsMatch(basicSetVal):
                        {
                            if (simulationMode)
                            {
                                Common.logger.Info("Simulation Mode, ignoring command");
                                break;
                            }
                            blockExit = true;
                            BasicOrBinarySet(basicRegex, basicSetVal);
                            blockExit = false;
                            break;
                        }
                    case var retryVal when retryRegex.IsMatch(retryVal):
                        blockExit = true;
                        BasicOrBinarySetRetry(retryRegex, retryVal);
                        blockExit = false;
                        break;
                    case var basicGetVal when basicGetRegex.IsMatch(basicGetVal):
                        {
                            blockExit = true;
                            var match = basicGetRegex.Match(basicGetVal);
                            var val = match.Groups[2].Value;
                            var nodeId = byte.Parse(val);

                            var success = false;
                            bool ret;
                            if (match.Groups[1].Value == "basic")
                            {
                                success = Common.GetBasic(Program.controller, nodeId, out ret);
                            }
                            else
                            {
                                success = Common.GetBinary(Program.controller, nodeId, out ret);
                            }
                            Common.logger.Info("{0}successful!", success ? "" : "not ");
                            if (success)
                            {
                                Common.logger.Info("value is: {0}", ret);
                            }
                            blockExit = false;
                            break;
                        }
                    case var configVal when ConfigCommand.IsMatch(configVal):
                        {
                            var nodeId = ConfigCommand.GetNodeId(configVal);
                            bool retry = ConfigCommand.IsRetry(configVal);
                            string profile = ConfigCommand.GetProfile(configVal);
                            bool isReadConfig = ConfigCommand.IsReadConfig(configVal);

                            currentCommand = new ConfigCommand(Program.controller, nodeId, Program.configList, retry, profile, isReadConfig);
                            break;
                        }
                    case var cmd when wakeUpRegex.IsMatch(cmd):
                        WakeUp(wakeUpRegex, cmd);
                        break;
                    case var cmdVal when wakeUpCapRegex.IsMatch(cmdVal):
                        GetWakeUpCapabilities(wakeUpCapRegex, cmdVal);
                        break;
                    case var replaceVal when ReplaceCommand.IsMatch(replaceVal):
                        {
                            var nodeId = ReplaceCommand.GetNodeId(replaceVal);
                            var profile = ReplaceCommand.GetProfile(replaceVal);
                            currentCommand = new ReplaceCommand(Program.controller, nodeId, Program.configList, profile);
                            break;
                        }
                    case var simulateVal when SimulateHelper.MatchesSimulate(simulateVal):
                        {
                            var helper = new SimulateHelper(simulateVal, Program.controller);
                            helper.CreateCommand();

                            if (helper.Command != null)
                            {
                                OutputManager.HandleCommand(helper.Command, helper.NodeId, 1);
                            }
                            break;
                        }
                    case var simulateVal when SimulateHelper.MatchesSimulateOnOff(simulateVal):
                        {
                            var helper = new SimulateHelper(simulateVal, Program.controller);
                            simulationMode = helper.GetSimulationMode();
                            listenComand.SimulationMode = simulationMode;
                            Common.logger.Info($"Simulate Mode {simulationMode}");
                            break;
                        }
                    case var setpointVal when rtr_setpointRegex.IsMatch(setpointVal):
                        if (simulationMode)
                        {
                            Common.logger.Info("Simulation Mode, ignoring command");
                            break;
                        }
                        ThermostatSetpoint(rtr_setpointRegex, setpointVal);
                        break;
                    case var modeVal when rtr_modeRegex.IsMatch(modeVal):
                        if (simulationMode)
                        {
                            Common.logger.Info("Simulation Mode, ignoring command");
                            break;
                        }
                        ThermostatMode(rtr_modeRegex, modeVal);
                        break;
                    case var operatingStateVal when rtr_operatingStateRegex.IsMatch(operatingStateVal):
                        if (simulationMode)
                        {
                            Common.logger.Info("Simulation Mode, ignoring command");
                            break;
                        }
                        ThermostatOperatingState(rtr_operatingStateRegex, operatingStateVal);
                        break;
                    default:
                        Common.logger.Warn("unknown command - " + HelpHint);
                        break;
                }
                if (currentCommand == null)
                {
                    continue;
                }
                listenComand.Active = false;
                var commandSuccess = currentCommand.Start();
                if (!commandSuccess && currentCommand.Retry)
                {
                    Common.logger.Info($"adding \"{input.Trim().ToLower()}\" for {currentCommand.NodeId} to queue");
                    queueCommand.AddToMap(currentCommand.NodeId, input.Trim().ToLower());
                }
                currentCommand = null;
                listenComand.Active = true;
            } while (Active && !oneShot);

            while (!listenComand.Active)
            {
                Thread.Sleep(100);
            }
            inputManager.Interrupt();
            listenComand.Stop();
            Common.logger.Info("goodby master...");
            return true;
        }

        private void Backup(bool readBigEeprom)
        {
            if (simulationMode)
            {
                Common.logger.Info("Simulation Mode, ignoring command");
                return;
            }
            LogHomeId();
            if (port == RAZBERRY_PORT)
            {
                Common.logger.Error($"backup not possible with port {port}");
                return;
            }
            blockExit = true;
            var result = Common.ReadNVRam(Program.controller, out byte[] eeprom, readBigEeprom);
            if (result)
            {
                string homeId = Tools.GetHexShort(Program.controller.HomeId).ToLower();
                string dateStr = DateTime.Now.ToString("yyyy-MM-dd-HH-mm");
                File.WriteAllBytes("eeprom.bin", eeprom);
                File.WriteAllBytes($"eeprom-{homeId}-{dateStr}.bin", eeprom);
                Common.logger.Info("Result is {0}", result);
            }
            blockExit = false;
        }

        public static string LogHomeId()
        {
            // The HomeId identifying the z-wave network.
            // it should be unique for each controller.
            if (Program.controller != null)
            {
                string homeId = Tools.GetHex(Program.controller.HomeId);
                Common.logger.Info($"HomeId: {homeId}");
                return homeId;
            }
            else
            {
                Common.logger.Warn("HomeId not available in simulation mode");
                return "";
            }
        }

        public static void LogIncludedNodes()
        {
            if (Program.controller != null)
            {
                var nodes = string.Join(", ", Program.controller.IncludedNodes);
                Common.logger.Info("Included nodes ({0}): {1}", Program.controller.IncludedNodes.Length, nodes);
            }
            else
            {
                Common.logger.Warn("included nodes not available in simulation mode");
            }
        }

        private static void ShowProfiles()
        {
            String profiles = Common.GetProfiles(Program.configList);
            Common.logger.Info($"Available profiles: {profiles}");
        }

        private void ShowHelp()
        {
            var help = new StringBuilder();
            help.AppendLine("Available commands:");
            help.AppendLine("  backup [256k]:                       creates a backup eeprom.bin file in the hyper directory");
            help.AppendLine("  basic nodeId:                        queries the state of a device, for example a relais");
            help.AppendLine("  basic nodeId {true|false}:           switches a device on or off, for example a relais");
            help.AppendLine("  binary nodeId:                       queries the state of a device using binary command class");
            help.AppendLine("  binary nodeId {true|false}:          switches a device on or off using binary command class");
            help.AppendLine("  cancel:                              cancels the current command (ClientTCP only, use Ctrl-C in hyper if a command is active)");
            help.AppendLine("  config nodeId [profile [param]][!]:  configures the device, optionally using a device profile");
            help.AppendLine("  debug {true|false}:                  enable or disable extra debug informations");
            help.AppendLine("  deleteoldevents [maxAge] [maxDaysToDelete]: deletes events older than maxAge in days, but only for a limited amount of days");
            help.AppendLine("  exclude:                             excludes a device");
            help.AppendLine("  help:                                shows this help");
            help.AppendLine("  homeid:                              show the z-wave HomeId");
            help.AppendLine("  include [profile [param]]:           starts inclusion and configuration, optionally using a device profile");
            help.AppendLine("  included:                            shows  list of included devices (may not be up to date afer include/exclude");
            help.AppendLine("  listen {start|stop|filter nodeID}:   Filters the event output. Example listen filter 12");
            help.AppendLine("  ping nodeId:                         checks if a device is reachable");
            help.AppendLine("  queue nodeID[,nodeID]... {config|readconfig} [profile [param]]: configures one or more devices at next wake up");
            help.AppendLine("  readconfig {nodeId} [profile [param]][!]: reads the configuation of the device");
            help.AppendLine("  reload:                              reload configuration files");
            help.AppendLine("  replace nodeId [profile [param]]:    checks existing nodeId, then replaces the device, optionally using a device profile");
            help.AppendLine("  reset!:                              resets the stick");
            help.AppendLine("  restore [filename]!:                 restores from eeprom.bin or another file in the hyper directory");
            help.AppendLine("  rtr_operating_state nodeId:          gets the thermostat operating state (1 is heating, 0 is off)");
            help.AppendLine("  rtr_mode nodeId [mode]:              gets or sets the thermostat mode (1 is heating, 0 is off)");
            help.AppendLine("  rtr_setpoint nodeId [temperature]:   gets or sets the thermostat setpoint in degrees Celcius");
            help.AppendLine("  show nodeId [count] [event]:         shows last events from the devices, optionally filtered. Example: show 2 10 battery_report");
            help.AppendLine("  wakeup nodeId:                       gets the wake up intervall of a device, in seconds");
            help.AppendLine("  wakeup nodeId value:                 sets the wake up intervall of a device, in seconds");
            help.AppendLine("  simulate nodeId {bin|bw|ft|mk|rtr|t} [channel] {true|false}: simulates a sensor event");
            help.AppendLine("  simulate nodeId scene sceneNumber:   simulates central scene");
            help.AppendLine("  simulate nodeId battery level:       simulates battery report");
            help.AppendLine("  simulate nodeId {humidity|temperature|setpoint} value:      simulates humidity or temperature report");
            help.AppendLine("  stop:                                in ClientTCP: stops the current command, or hyper if no command is executing. Same as Ctrl+C in hyper");
            Common.logger.Info(help.ToString());
        }

        private void ReadProgramConfig()
        {
            retryDelaysForBasic = Program.programConfig.GetIntListValueOrDefault(RETRYDELAYSFORBASIC_KEY, RETRYDELAYSFORBASIC_DEFAULT);
            if (retryDelaysForBasic.Length > 0)
            {
                Common.logger.Info("retryDelaysForBasic: {0}", string.Join<int>(" ", retryDelaysForBasic));
            }
            else
            {
                Common.logger.Info("retries disabled for basic");
            }
            OutputManager.ReadProgramConfig();
        }

        private void BasicOrBinarySet(Regex basicRegex, string basicSetVal)
        {
            var match = basicRegex.Match(basicSetVal);
            var action = match.Groups[1].Value;
            var val = match.Groups[2].Value;
            var nodeId = byte.Parse(val);
            val = match.Groups[3].Value;
            var value = bool.Parse(val);

            if (basicRequestNumbers.ContainsKey(nodeId))
            {
                ++basicRequestNumbers[nodeId];
            }
            else
            {
                basicRequestNumbers.Add(nodeId, 0);
            }
            BasicOrBinary(action, nodeId, value, 0, basicRequestNumbers[nodeId]);
        }

        private void BasicOrBinarySetRetry(Regex retryRegex, string retryVal)
        {
            var match = retryRegex.Match(retryVal);
            var action = match.Groups[1].Value;
            var val = match.Groups[2].Value;
            var nodeId = byte.Parse(val);
            val = match.Groups[3].Value;
            var value = bool.Parse(val);
            var retryNumVal = match.Groups[4].Value;
            int retryNum = 0;
            var retryRequestNumberVal = match.Groups[5].Value;
            byte requestNumber = 0;

            //it is a retry. the number of the retry is explicit in the command
            if (!string.IsNullOrEmpty(retryNumVal))
            {
                int.TryParse(retryNumVal, out retryNum);
            }
            if (!string.IsNullOrEmpty(retryRequestNumberVal))
            {
                byte.TryParse(retryRequestNumberVal, out requestNumber);
            }
            if (requestNumber == basicRequestNumbers[nodeId])
            {
                BasicOrBinary(action, nodeId, value, retryNum, requestNumber);
            }
            else
            {
                //retrying and another command came in the meantime. abort the retries!
                Common.logger.Info("node {0}: {1} retry cancelled for obsolete command", nodeId, action);
                Common.logger.Debug("node {0}, retryRequestNumber {1}, current requestNummer {2}",
                    nodeId, requestNumber, basicRequestNumbers[nodeId]);
            }
        }

        private void BasicOrBinary(string action, byte nodeId, bool value, int retryNum, byte requestNumber)
        {
            var success = false;
            if (action == "basic")
            {
                success = Common.SetBasic(Program.controller, nodeId, value);
            }
            else if (action == "binary")
            {
                success = Common.SetBinary(Program.controller, nodeId, value);
            }
            LogActionOutcome(action, nodeId, success, retryNum);
            if (!success && (retryNum < retryDelaysForBasic.Length))
            {
                int delay = retryDelaysForBasic[retryNum];
                string newCmd = action + "retry " + nodeId + " " + value + " " + ++retryNum + " " + requestNumber;
                InjectCommandWithDelay(newCmd, delay);
            }
        }

        private void LogActionOutcome(string action, byte nodeId, bool success, int retryNum = int.MaxValue)
        {
            if (success)
            {
                Common.logger.Info("node {0}: {1} successful!", nodeId, action);
            }
            else if (retryNum < retryDelaysForBasic.Length)
            {
                Common.logger.Warn("node {0}: {1} failed!", nodeId, action);
            }
            else
            {
                Common.logger.Error("node {0}: {1} failed!", nodeId, action);
            }
        }

        private void InjectCommandWithDelay(string newCmd, int delay)
        {
            // waits (asynchronoulsy), then queues the new command.
            // does not block the command thread during the delay
            Common.logger.Info($"injecting retry command with {delay} seconds delay");

            Task task = Task.Delay(delay * 1000)
                .ContinueWith(t => inputManager.InjectCommand(newCmd));
        }

        private void ThermostatMode(Regex rtr_modeRegex, string modeVal)
        {
            var match = rtr_modeRegex.Match(modeVal);
            var val = match.Groups[1].Value;
            var nodeId = byte.Parse(val);
            val = match.Groups[2].Value;
            if (string.IsNullOrEmpty(val))
            {
                bool success = Common.RequestThermostatMode(Program.controller, nodeId);
                LogActionOutcome("get rtr_mode", nodeId, success);
            }
            else
            {
                byte value = 0;
                if (byte.TryParse(val, out value))
                {
                    bool success = Common.ThermostatMode(Program.controller, nodeId, value);
                    LogActionOutcome("rtr_mode", nodeId, success);
                }
            }
        }

        private void ThermostatOperatingState(Regex rtr_operatingStateRegex, string operatingStateVal)
        {
            var match = rtr_operatingStateRegex.Match(operatingStateVal);
            var val = match.Groups[1].Value;
            var nodeId = byte.Parse(val);
            bool success = Common.RequestThermostatOperatingState(Program.controller, nodeId);
            LogActionOutcome("get rtr_operating_state", nodeId, success);
        }

        private void ThermostatSetpoint(Regex rtr_setpointRegex, string setpointVal)
        {
            var match = rtr_setpointRegex.Match(setpointVal);
            var val = match.Groups[1].Value;
            var nodeId = byte.Parse(val);
            val = match.Groups[2].Value;
            if (string.IsNullOrEmpty(val))
            {
                bool success = Common.RequestThermostatSetpoint(Program.controller, nodeId);
                LogActionOutcome("get rtr_setpoint", nodeId, success);
            }
            else
            {
                float value = 0;
                //should not depend on the locale because it will be sent from alfred
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    bool success = Common.ThermostatSetpoint(Program.controller, nodeId, value);
                    LogActionOutcome("rtr_setpoint", nodeId, success);
                }
            }
        }

        private static void WakeUp(Regex wakeUpRegex, string cmd)
        {
            var groups = wakeUpRegex.Match(cmd).Groups;
            byte nodeId = byte.Parse(groups[1].Value);
            string argIntervalSeconds = groups[2].Value;
            if (string.IsNullOrEmpty(argIntervalSeconds))
            {
                Common.GetWakeUp(Program.controller, nodeId);
            }
            else
            {
                int timeoutValue = int.Parse(argIntervalSeconds);
                bool success = Common.SetWakeUp(Program.controller, nodeId, timeoutValue);
                if (success)
                {
                    Common.GetWakeUp(Program.controller, nodeId);
                }
            }
        }

        private static void GetWakeUpCapabilities(Regex wakeUpCapRegex, string cmdVal)
        {
            string argNodeId = wakeUpCapRegex.Match(cmdVal).Groups[1].Value;
            byte nodeId = byte.Parse(argNodeId);
            Common.GetWakeUpCapabilities(Program.controller, nodeId);
        }

        public override void Stop()
        {
            if (currentCommand != null)
            {
                Common.logger.Info("stoppping current command!");
                currentCommand.Stop();
            }
            else
            {
                Common.logger.Info("stopping interactive mode");
                Common.logger.Info("press any key to exit");
                Active = false;
            }
        }
    }
}