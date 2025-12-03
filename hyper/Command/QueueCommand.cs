using hyper.Command;
using hyper.commands;
using hyper.config;
using hyper.Database.DAO;
using hyper.Helper;
using hyper.Inputs;
using hyper.Output;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Utils;
using ZWave;
using ZWave.BasicApplication.Devices;
using ZWave.CommandClasses;

namespace hyper
{
    public class QueueCommand : BaseCommand
    {
        private static Regex regex = new Regex(@$"^queue\s*({OneTo255Regex}+(?:\s*,\s*{OneTo255Regex}+)*)\s*(config|readconfig)\s*({ProfileRegex})?");

        private readonly Controller controller;
        private List<ConfigItem> configList;
        private InputManager inputManager;
        private EventDAO eventDao = new EventDAO();
        private readonly object lockObject = new object();
        private Dictionary<byte, DateTime> lastBatteryTimes = new Dictionary<byte, DateTime>();
        private int minBatteryIntervall;

        public static bool IsMatch(string cmd)
        {
            return regex.IsMatch(cmd);
        }

        public static byte[] GetNodeIds(string command)
        {
            var match = regex.Match(command);
            var nodeIdsAsStr = match.Groups[1].Value.Split(",");
            var ret = new byte[nodeIdsAsStr.Length];
            for (int i = 0; i < nodeIdsAsStr.Length; i++)
            {
                ret[i] = byte.Parse(nodeIdsAsStr[i].Trim());
            }
            return ret;
        }

        public static string GetCommand(string queueVal)
        {
            return regex.Match(queueVal).Groups[2].Value;
        }

        public static string GetParameter(string queueVal)
        {
            return regex.Match(queueVal).Groups[3].Value;
        }

        public QueueCommand(Controller controller, List<ConfigItem> configList, InputManager inputManager)
        {
            this.controller = controller;
            this.configList = configList;
            this.inputManager = inputManager;
        }

        public bool Active
        {
            get; set;
        } = false;

        private Dictionary<byte, SortedSet<string>> nodeToCommandMap = new Dictionary<byte, SortedSet<string>>();

        //private readonly BlockingCollection<Action> queueItems = new BlockingCollection<Action>();
        private ActionToken dataListener;

        private ActionToken controllerListener;

        public void AddToMap(byte nodeId, string command)
        {
            var sortedSet = nodeToCommandMap.GetValueOrDefault(nodeId, new SortedSet<string>());
            sortedSet.Add(command);
            nodeToCommandMap[nodeId] = sortedSet;
        }

        //public void AddToQueue(Action action)
        //{
        //    queueItems.Add(action);
        //}

        public override bool Start()
        {
            Common.logger.Info("-----------");
            Common.logger.Info("Listening mode");
            Common.logger.Info("-----------");

            Common.logger.Info("Loading available command classes...");
            var assembly = typeof(COMMAND_CLASS_BASIC).GetTypeInfo().Assembly;
            var commandClasses = Common.GetAllCommandClasses(assembly, "CommandClasses");
            Common.logger.Info("Got {0} command classes", commandClasses.Count);
            var nestedCommandClasses = Common.GetAllNestedCommandClasses(commandClasses.Values);
            Common.logger.Info("Got all inner command classes for {0} command classes", commandClasses.Count);
            ReadProgramConfig();
            Common.logger.Info("Listening...");

            //byte[] numArray = File.ReadAllBytes(@"C:\Users\james\Desktop\tmp\MultiSensor 6_OTA_EU_A_V1_13.exe");
            //int length = (int)numArray[numArray.Length - 4] << 24 | (int)numArray[numArray.Length - 3] << 16 | (int)numArray[numArray.Length - 2] << 8 | (int)numArray[numArray.Length - 1];
            //byte[] flashDataB = new byte[length];
            //Array.Copy((Array)numArray, numArray.Length - length - 4 - 4 - 256, (Array)flashDataB, 0, length);
            //List<byte> flashData = new List<byte>(flashDataB);

            dataListener = controller.ListenData((x) =>
            {
                lock (lockObject)
                {
                    var _commandClass = commandClasses.TryGetValue(x.Command[0], out var commandClass);
                    if (!_commandClass)
                    {
                        Common.logger.Error("node id: {1} - command class {0} not found!", x.Command[0], x.SrcNodeId);
                        return;
                    }
                    var _nestedDict = nestedCommandClasses.TryGetValue(commandClass, out var nestedDict);
                    if (!_nestedDict)
                    {
                        Common.logger.Error("node id: {1} - nested command classes for command class {0} not found!", commandClass.Name, x.SrcNodeId);
                        return;
                    }
                    var _nestedType = nestedDict.TryGetValue(x.Command[1], out Type nestedType);
                    if (!_nestedType)
                    {
                        Common.logger.Error("node id: {2} - nested command class {0} for command class {1} not found!", x.Command[1], commandClass.Name, x.SrcNodeId);
                        return;
                    }

                    //  Common.logger.Info("{0}: {2}:{3} from node {1}", x.TimeStamp, x.SrcNodeId, _commandClass ? commandClass.Name : string.Format("unknown(id:{0})", x.Command[0]), _nestedType ? nestedType.Name : string.Format("unknown(id:{0})", x.Command[1]));

                    if (commandClass == null)
                    {
                        Common.logger.Error("command class is null!");
                        return;
                    }
                    if (nestedType == null)
                    {
                        Common.logger.Error("nested type is null!");
                        return;
                    }

                    //     var dummyInstance = Activator.CreateInstance(nestedType);

                    var implicitCastMethod =
              nestedType.GetMethod("op_Implicit",
                                   new[] { x.Command.GetType() });

                    if (implicitCastMethod == null)
                    {
                        Common.logger.Warn("byteArray to {0} not possible!", nestedType.Name);
                        return;
                    }
                    var report = implicitCastMethod.Invoke(null, new[] { x.Command });

                    switch (report)
                    {
                        case COMMAND_CLASS_WAKE_UP_V2.WAKE_UP_NOTIFICATION _:

                            var commandsPresent = nodeToCommandMap.TryGetValue(x.SrcNodeId, out SortedSet<string> commands);
                            if (!commandsPresent)
                            {
                                RequestBatteryIfNeeded(x.SrcNodeId);
                                return;
                            }

                            var command = commands.First();
                            Common.logger.Warn($"injecting {command}");
                            inputManager.InjectCommand(command);
                            commands.Remove(commands.First());
                            if (commands.Count == 0)
                            {
                                nodeToCommandMap.Remove(x.SrcNodeId);
                            }

                            break;

                        default:
                            break;
                    }
                }
            });

            controllerListener = controller.HandleControllerUpdate((r) =>
            {
                var commandsPresent = nodeToCommandMap.TryGetValue(r.NodeId, out SortedSet<string> commands);
                if (!commandsPresent)
                {
                    RequestBatteryIfNeeded(r.NodeId);
                    return;
                }

                var command = commands.First();
                Common.logger.Warn($"injecting {command}");
                inputManager.InjectCommand(command);
                commands.Remove(commands.First());
                if (commands.Count == 0)
                {
                    nodeToCommandMap.Remove(r.NodeId);
                }
            });

            //Active = true;
            //while (!queueItems.IsCompleted)
            //{
            //    try
            //    {
            //        var action = queueItems.Take();
            //        if (Active)
            //            action();
            //    }
            //    catch (InvalidOperationException) { }
            //}
            dataListener.WaitCompletedSignal();
            controllerListener.WaitCompletedSignal();
            //Active = false;
            Common.logger.Info("Listening done!");
            return true;
        }

        public void ReadProgramConfig()
        {
            //default 0 to get new battery value if batteries are changed for Door/Window Sensor
            // 17 hours would be slighty smaller than the common 18 hours wakeup interval
            minBatteryIntervall = Program.programConfig.GetIntValueOrDefault("minBatteryIntervall", 0);
        }

            private void RequestBatteryIfNeeded(byte nodeId)
        {
            if (NeedsBatteryValue(nodeId))
            {
                inputManager.InjectCommand($"battery {nodeId}");
                UpdateLastBatteryTime(nodeId);
            }
            else
            {
                Common.logger.Info($"id: {nodeId} - battery value not needed");
            }
        }

        private void UpdateLastBatteryTime(byte srcNodeId)
        {
            lastBatteryTimes[srcNodeId] = DateTime.Now;
        }

        /// <summary>
        /// Checks if the last battery query for the device is old enough
        /// Previous code checked in the database, but it is too slow, the device often goes to sleep in the meantime.
        /// </summary>
        /// <param name="srcNodeId"></param>
        /// <returns></returns>
        private bool NeedsBatteryValue(byte srcNodeId)
        {
            DateTime lastTime;
            if (lastBatteryTimes.TryGetValue(srcNodeId, out lastTime))
            {
                var age = DateTime.Now - lastTime;
                return age > new TimeSpan(minBatteryIntervall, 0, 0);
            }
            return true;
        }

        //private void OnCommand(object sender, string e)
        //{
        //    Common.logger.Info("recevied from da console: " + e);
        //    if(e == "configure 14")
        //    {
        //        queueItems.Add(() => {
        //            // Common.RequestBatteryReport(controller, x.SrcNodeId)
        //                  new ConfigCommand(controller, 14, configList).Start();

        //        });
        //    }
        //}

        public override void Stop()
        {
            Common.logger.Info("stop listening!");
            dataListener?.SetCancelled();
            //dataListener?.SetCompletedSignal();
            controllerListener?.SetCancelled();
            // controllerListener?.SetCompletedSignal();
            // queueItems?.CompleteAdding();
        }

        internal void UpdateConfig(List<ConfigItem> configList)
        {
            this.configList = configList;
        }
    }
}