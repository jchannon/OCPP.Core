﻿/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2021 dallmann consulting GmbH.
 * All Rights Reserved.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Schema;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP16;

namespace OCPP.Core.Server
{
    public partial class ControllerBase
    {
        /// <summary>
        /// Internal string for OCPP protocol version
        /// </summary>
        protected virtual string ProtocolVersion { get;  }

        /// <summary>
        /// Configuration context for reading app settings
        /// </summary>
        protected IConfiguration Configuration { get; set; }

        /// <summary>
        /// Chargepoint status
        /// </summary>
        protected ChargePointStatus ChargePointStatus { get; set; }

        /// <summary>
        /// ILogger object
        /// </summary>
        protected ILogger Logger { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        public ControllerBase(IConfiguration config, ILoggerFactory loggerFactory, ChargePointStatus chargePointStatus)
        {
            Configuration = config;

            if (chargePointStatus != null)
            {
                ChargePointStatus = chargePointStatus;
            }
            else
            {
                Logger.LogError("New ControllerBase => empty chargepoint status");
            }
        }

        /// <summary>
        /// Deserialize and validate JSON message (if schema file exists)
        /// </summary>
        protected T DeserializeMessage<T>(OCPPMessage msg)
        {
            string path = Assembly.GetExecutingAssembly().Location;
            string codeBase = Path.GetDirectoryName(path);

            bool validateMessages = Configuration.GetValue<bool>("ValidateMessages", false);

            string schemaJson = null;
            if (validateMessages && 
                !string.IsNullOrEmpty(codeBase) && 
                Directory.Exists(codeBase))
            {
                string msgTypeName = typeof(T).Name;
                string filename = Path.Combine(codeBase, $"Schema{ProtocolVersion}", $"{msgTypeName}.json");
                if (File.Exists(filename))
                {
                    Logger.LogTrace("DeserializeMessage => Using schema file: {0}", filename);
                    schemaJson = File.ReadAllText(filename);
                }
            }

            JsonTextReader reader = new JsonTextReader(new StringReader(msg.JsonPayload));
            JsonSerializer serializer = new JsonSerializer();

            if (!string.IsNullOrEmpty(schemaJson))
            {
                JSchemaValidatingReader validatingReader = new JSchemaValidatingReader(reader);
                validatingReader.Schema = JSchema.Parse(schemaJson);

                IList<string> messages = new List<string>();
                validatingReader.ValidationEventHandler += (o, a) => messages.Add(a.Message);
                T obj = serializer.Deserialize<T>(validatingReader);
                if (messages.Count > 0)
                {
                    foreach (string err in messages)
                    {
                        Logger.LogWarning("DeserializeMessage {0} => Validation error: {1}", msg.Action, err);
                    }
                    throw new FormatException("Message validation failed");
                }
                return obj;
            }
            else
            {
                // Deserialization WITHOUT schema validation
                Logger.LogTrace("DeserializeMessage => Deserialization without schema validation");
                return serializer.Deserialize<T>(reader);
            }
        }


        /// <summary>
        /// Helper function for creating and updating the ConnectorStatus in then database
        /// </summary>
        protected bool UpdateConnectorStatus(int connectorId, string status, DateTimeOffset? statusTime, double? meter, DateTimeOffset? meterTime)
        {
            try
            {
                using (OCPPCoreContext dbContext = new OCPPCoreContext(Configuration))
                {
                    ConnectorStatus connectorStatus = dbContext.Find<ConnectorStatus>(ChargePointStatus.Id, connectorId);
                    if (connectorStatus == null)
                    {
                        // no matching entry => create connector status
                        connectorStatus = new ConnectorStatus();
                        connectorStatus.ChargePointId = ChargePointStatus.Id;
                        connectorStatus.ConnectorId = connectorId;
                        Logger.LogTrace("UpdateConnectorStatus => Creating new DB-ConnectorStatus: ID={0} / Connector={1}", connectorStatus.ChargePointId, connectorStatus.ConnectorId);
                        dbContext.Add<ConnectorStatus>(connectorStatus);
                    }

                    if (!string.IsNullOrEmpty(status))
                    {
                        connectorStatus.LastStatus = status;
                        connectorStatus.LastStatusTime = ((statusTime.HasValue) ? statusTime.Value : DateTimeOffset.UtcNow).DateTime;
                    }

                    if (meter.HasValue)
                    {
                        connectorStatus.LastMeter = meter.Value;
                        connectorStatus.LastMeterTime = ((meterTime.HasValue) ? meterTime.Value : DateTimeOffset.UtcNow).DateTime;
                    }
                    dbContext.SaveChanges();
                    Logger.LogInformation("UpdateConnectorStatus => Save ConnectorStatus: ID={0} / Connector={1} / Status={2} / Meter={3}", connectorStatus.ChargePointId, connectorId, status, meter);
                    return true;
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "UpdateConnectorStatus => Exception writing connector status (ID={0} / Connector={1}): {2}", ChargePointStatus?.Id, connectorId, exp.Message);
            }

            return false;
        }

        /// <summary>
        /// Clean charge tag Id from possible suffix ("..._abc")
        /// </summary>
        protected static string CleanChargeTagId(string rawChargeTagId, ILogger logger)
        {
            string idTag = rawChargeTagId;

            // KEBA adds the serial to the idTag ("<idTag>_<serial>") => cut off suffix
            if (!string.IsNullOrWhiteSpace(rawChargeTagId))
            {
                int sep = rawChargeTagId.IndexOf('_');
                if (sep >= 0)
                {
                    idTag = rawChargeTagId.Substring(0, sep);
                    logger.LogTrace("CleanChargeTagId => Charge tag '{0}' => '{1}'", rawChargeTagId, idTag);
                }
            }

            return idTag;
        }

        protected static DateTimeOffset MaxExpiryDate
        {
            get
            {
                return new DateTime(2199, 12, 31);
            }
        }
    }
}
