using hyper.Helper;
using hyper.Helper.Extension;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ZWave.CommandClasses;

namespace hyper.Command
{
    public class SimulateHelper
    {
        //z.B. "simulate 3 mk true" => door open
        //"simulate 4 t 1 true" => button 1 on for touch panel
        private static Regex simulateRegex = new Regex(
            @$"^simulate\s+({BaseCommand.OneTo255Regex})\s+(bin|bw|ft|mk|rtr|t|wakeup)\s*(1|2)?\s*(false|true)");
        private static Regex simulateOnOffRegex = new Regex(@$"^simulate\s+(false|true)"); //simulate true => simulation mode on
        private static Regex simulateSceneRegex = new Regex(
            @$"^simulate\s+({BaseCommand.OneTo255Regex})\s+(scene)\s*([1-6])\s*$");
        private static Regex simulateOrHumRegex = new Regex(
            @$"^simulate\s+({BaseCommand.OneTo255Regex})\s+(battery|humidity|temperature|setpoint)\s*(-?{BaseCommand.ZeroTo255Regex})\s*$");

        private Match match;
        private bool hasController;

        public static bool MatchesSimulate(string simulateVal)
        {
            return simulateVal.StartsWith("simulate") &&
                (
                    simulateRegex.IsMatch(simulateVal) ||
                    simulateSceneRegex.IsMatch(simulateVal) ||
                    simulateOrHumRegex.IsMatch(simulateVal)
                );
        }

        public static bool MatchesSimulateOnOff(string command)
        {
            return simulateOnOffRegex.IsMatch(command);
        }

        public SimulateHelper(string simulateVal, object controller)
        {
            if (simulateRegex.IsMatch(simulateVal)) {
                match = simulateRegex.Match(simulateVal);
            }
            else if (simulateSceneRegex.IsMatch(simulateVal))
            {
                match = simulateSceneRegex.Match(simulateVal);
            }
            else if (simulateOrHumRegex.IsMatch(simulateVal))
            {
                match = simulateOrHumRegex.Match(simulateVal);
            }
            else
            {
                match = simulateOnOffRegex.Match(simulateVal);
            }
            hasController = (controller != null);
        }

        public byte NodeId { get; private set;}
        public object Command { get; private set; }

        public void CreateCommand()
        {
            NodeId = byte.Parse(match.Groups[1].Value);
            var type = match.Groups[2].Value; //bin,bw,mk (or scene)
            var param3 = match.Groups[3].Value; // endpoint, sceneNumber, or battery value
            var value = match.Groups.Count > 4 ? bool.Parse(match.Groups[4].Value) : false;

            //command is always lower case
            switch (type)
            {
                case "bin":
                case "ft":
                    Command = new COMMAND_CLASS_BASIC_V2.BASIC_SET()
                    {
                        value = value ? (byte)255 : (byte)0

                    };
                    break;
                case "bw":
                    //there is a COMMAND_CLASS_NOTIFICATION_V8:NOTIFICATION_REPORT too,
                    //but only SENSOR_BINARY_REPORT is send to alfred
                    Command = new COMMAND_CLASS_SENSOR_BINARY_V2.SENSOR_BINARY_REPORT()
                    {
                        sensorValue = value ? (byte)255 : (byte)0
                    };
                    break;
                case "mk":
                    Command = new COMMAND_CLASS_NOTIFICATION_V8.NOTIFICATION_REPORT()
                    {
                        notificationType = (byte)NotificationType.AccessControl,
                        mevent = value ? (byte)AccessControlEvent.WindowDoorIsOpen : (byte)AccessControlEvent.WindowDoorIsClosed
                    };
                    break;
                case "t": //TPS412
                    Command = CreateMultiChannelBasicReportEncap(param3, value);
                    break;
                case "battery":
                    Command = new COMMAND_CLASS_BATTERY.BATTERY_REPORT()
                    {
                        batteryLevel = byte.Parse(param3)
                    };
                    break;
                case "humidity":
                    Command = new COMMAND_CLASS_SENSOR_MULTILEVEL_V11.SENSOR_MULTILEVEL_REPORT()
                    {
                        properties1 = new COMMAND_CLASS_SENSOR_MULTILEVEL_V11.SENSOR_MULTILEVEL_REPORT.Tproperties1
                        {
                            size = 1,
                            precision = 0
                        },
                        sensorType = 0x05,
                        sensorValue = { byte.Parse(param3) }
                    };
                    break;
                case "temperature":
                    Command = new COMMAND_CLASS_SENSOR_MULTILEVEL_V11.SENSOR_MULTILEVEL_REPORT()
                    {
                        properties1 = new COMMAND_CLASS_SENSOR_MULTILEVEL_V11.SENSOR_MULTILEVEL_REPORT.Tproperties1
                        {
                            size = 2,
                            precision = 2
                        },
                        sensorType = 0x01,
                        sensorValue = floatToBytes(param3, 2)
                    };
                    break;
                case "setpoint":
                    Command = new COMMAND_CLASS_THERMOSTAT_SETPOINT_V3.THERMOSTAT_SETPOINT_REPORT()
                    {

                        properties1 = new COMMAND_CLASS_THERMOSTAT_SETPOINT_V3.THERMOSTAT_SETPOINT_REPORT.Tproperties1
                        {
                            setpointType = 1, //Heating
                        },
                        properties2 = new COMMAND_CLASS_THERMOSTAT_SETPOINT_V3.THERMOSTAT_SETPOINT_REPORT.Tproperties2
                        {
                            size = 2,
                            precision = 1
                        },
                        value = floatToBytes(param3, 1)
                    };
                    break;
                case "rtr":
                    Command = new COMMAND_CLASS_THERMOSTAT_OPERATING_STATE_V2.THERMOSTAT_OPERATING_STATE_REPORT()
                    {
                        properties1 = new COMMAND_CLASS_THERMOSTAT_OPERATING_STATE_V2.THERMOSTAT_OPERATING_STATE_REPORT.Tproperties1
                        {
                            operatingState = value ? (byte)1 : (byte)0
                        }
                    };
                    break;
                case "scene":
                    Command = new COMMAND_CLASS_CENTRAL_SCENE_V3.CENTRAL_SCENE_NOTIFICATION()
                    {
                        sequenceNumber = 0, //normally incremented for each message
                        sceneNumber = byte.Parse(param3),
                        properties1 = new COMMAND_CLASS_CENTRAL_SCENE_V3.CENTRAL_SCENE_NOTIFICATION.Tproperties1
                        {
                            slowRefresh = 1
                        }
                    };
                    break;
                case "wakeup":
                    Command = new COMMAND_CLASS_WAKE_UP_V2.WAKE_UP_NOTIFICATION();
                    break;
                default:
                    Common.logger.Error($"simulate: type {type} not recognized!");
                    break;
            }
            if (Command != null)
            {
                Command.GetKeyValue(out Enums.EventKey eventKey, out float eventValue);
                Common.logger.Info($"id: {NodeId} - key: {eventKey} - value: {eventValue} (simulated)");
                Common.logger.Info($"simulate event - id: {NodeId} - key: {eventKey} - value: {eventValue}");
            }
        }

        private IList<byte> floatToBytes(string param3, int precision)
        {
            short shortValue = short.Parse(param3);
            short scaled = shortValue;
            if (precision == 1)
            {
                scaled = (short)(shortValue * 10);
            }
            if (precision == 2)
            {
                scaled = (short)(shortValue * 100);
            }
            return BitConverter.GetBytes(scaled).Reverse().ToArray();
        }

        public static COMMAND_CLASS_MULTI_CHANNEL_V4.MULTI_CHANNEL_CMD_ENCAP CreateMultiChannelBasicReportEncap(string endpointStr, bool value)
        {
            byte endpoint = byte.Parse(endpointStr);
            //sends 3 Messages:
            /*
            2022-03-14 16:41:27.7835 INFO 03/14/2022 16:41:27: COMMAND_CLASS_BASIC_V2:BASIC_SET from node 42
            2022-03-14 16:41:27.7835 INFO id: 42 - key: BASIC - value: 1
            2022-03-14 16:41:27.8018 INFO 03/14/2022 16:41:27: COMMAND_CLASS_BASIC_V2:BASIC_REPORT from node 42
            2022-03-14 16:41:27.8018 INFO id: 42 - key: STATE_ON - value: 1
            2022-03-14 16:41:27.8086 INFO same message or too soon! doing nothing
            2022-03-14 16:41:27.8086 INFO But different key: BASIC - STATE_ON
            2022-03-14 16:41:27.8211 INFO 03/14/2022 16:41:27: COMMAND_CLASS_MULTI_CHANNEL_V4:MULTI_CHANNEL_CMD_ENCAP from node 42
            2022-03-14 16:41:27.8211 INFO id: 42 - key: CHANNEL_1_STATE - value: 1
            */
            // {"properties1":{"sourceEndPoint":1,"res":0},"properties2":{"destinationEndPoint":1,"bitAddress":0},"commandClass":32,"command":3,"parameter":[255]}
            var cmd = new COMMAND_CLASS_MULTI_CHANNEL_V4.MULTI_CHANNEL_CMD_ENCAP()
            {
                commandClass = COMMAND_CLASS_BASIC_V2.ID,
                command = COMMAND_CLASS_BASIC_V2.BASIC_REPORT.ID,
                parameter = new List<byte>() { value ? (byte)255 : (byte)0 },
            };
            cmd.properties1.sourceEndPoint = endpoint;
            cmd.properties1.res = 0;
            cmd.properties2.destinationEndPoint = endpoint;
            cmd.properties2.bitAddress = 0;
            return cmd;
        }

        public bool GetSimulationMode()
        {
            if (match.Groups.Count == 2)
            {
                bool mode = bool.Parse(match.Groups[1].Value);
                if (hasController || mode)
                {
                    return mode;
                }
                else
                {
                    Common.logger.Warn("No Controller, cannot stop Simulation Mode.");
                    return true;
                }
            }
            return false;
        }
    }
}
