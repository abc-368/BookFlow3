using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts;

namespace BookFlow.Shared.IPC
{
    public class ControlPipeServer : IDisposable
    {
        private readonly string _pipeName;
        public static Action<string> Logger { get; set; }
        public event Func<ControlMessage, Task<object>> MessageReceived;
        public event Action ClientDisconnected;

        public ControlPipeServer(string pipeName)
        {
            _pipeName = pipeName;
            _ = ConnectionLoop();
        }

        private async Task ConnectionLoop()
        {
            while (true)
            {
                try
                {
                    var pipeInstance = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                    await pipeInstance.WaitForConnectionAsync();
                    _ = Task.Run(() => HandleClientSession(pipeInstance));
                }
                catch (Exception ex)
                {
                    Logger?.Invoke($"[ControlPipeServer] Error in connection loop: {ex.Message}");
                    await Task.Delay(2000);
                }
            }
        }

        private async Task HandleClientSession(NamedPipeServerStream clientPipe)
        {
            using (clientPipe)
            {
                while (clientPipe.IsConnected)
                {
                    try
                    {
                        var buffer = new byte[65536];
                        var bytesRead = await clientPipe.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;
                        var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        var message = DeserializeControlMessage(json);
                        if (message.Type == ControlMessage.RequestType.Disconnect) break;
                        if (MessageReceived != null)
                        {
                            var response = await MessageReceived.Invoke(message);
                            if (response != null) await SendResponse(clientPipe, response);
                        }
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger?.Invoke($"[ControlPipeServer] Session error: {ex.Message}");
                        break;
                    }
                }
            }
            ClientDisconnected?.Invoke();
        }

        private async Task SendResponse(NamedPipeServerStream clientPipe, object response)
        {
            try
            {
                var json = SerializeResponse(response);
                var buffer = Encoding.UTF8.GetBytes(json);
                await clientPipe.WriteAsync(buffer, 0, buffer.Length);
                await clientPipe.FlushAsync();
            }
            catch (Exception ex)
            {
                Logger?.Invoke($"[ControlPipeServer] Error sending response: {ex.Message}");
            }
        }

        private string SerializeResponse(object response)
        {
            if (response is TickerDictionaryResponse tickerDict)
            {
                var sb = new StringBuilder();
                sb.Append('{');
                sb.Append("\"TickerDictionary\":{");
                bool first = true;
                foreach (var kvp in tickerDict.TickerDictionary)
                {
                    if (!first) sb.Append(',');
                    sb.AppendFormat("\"{0}\":\"{1}\"", kvp.Key, Escape(kvp.Value));
                    first = false;
                }
                sb.Append('}');
                sb.AppendFormat(",\"Timestamp\":\"{0}\"", tickerDict.Timestamp.ToString("o"));
                sb.AppendFormat(",\"RequestId\":\"{0}\"", Escape(tickerDict.RequestId ?? string.Empty));
                sb.Append('}');
                return sb.ToString();
            }
            if (response is TickerRegistrationResponse reg)
            {
                return $"{{\"TickerId\":{reg.TickerId},\"Success\":{(reg.Success ? "true" : "false")},\"Message\":\"{Escape(reg.Message)}\",\"Timestamp\":\"{reg.Timestamp:o}\",\"RequestId\":\"{Escape(reg.RequestId)}\"}}";
            }
            if (response is OrderStatusMessage order)
            {
                return $"{{\"ClientOrderId\":\"{Escape(order.ClientOrderId)}\",\"Status\":{(int)order.Status},\"Message\":\"{Escape(order.Message)}\",\"FillPrice\":{order.FillPrice},\"FillQuantity\":{order.FillQuantity},\"Timestamp\":\"{order.Timestamp:o}\"}}";
            }
            if (response is PortfolioStateMessage portfolio)
            {
                var sb = new StringBuilder();
                sb.Append('{');
                sb.AppendFormat("\"Timestamp\":\"{0}\",", portfolio.Timestamp.ToString("o"));
                if (portfolio.Account != null)
                {
                    sb.Append("\"Account\":{");
                    sb.AppendFormat("\"AccountName\":\"{0}\",", Escape(portfolio.Account.AccountName));
                    sb.AppendFormat("\"BuyingPower\":{0},", portfolio.Account.BuyingPower);
                    sb.AppendFormat("\"CashValue\":{0},", portfolio.Account.CashValue);
                    sb.AppendFormat("\"UnrealizedPnL\":{0},", portfolio.Account.UnrealizedPnL);
                    sb.AppendFormat("\"RealizedPnL\":{0},", portfolio.Account.RealizedPnL);
                    sb.AppendFormat("\"NetLiquidation\":{0},", portfolio.Account.NetLiquidation);
                    sb.AppendFormat("\"InitialMargin\":{0},", portfolio.Account.InitialMargin);
                    sb.AppendFormat("\"MaintenanceMargin\":{0},", portfolio.Account.MaintenanceMargin);
                    sb.AppendFormat("\"ExcessEquity\":{0},", portfolio.Account.ExcessEquity);
                    sb.AppendFormat("\"Commission\":{0}}", portfolio.Account.Commission);
                }
                else sb.Append("\"Account\":{}");
                sb.Append(",\"Orders\":[");
                for (int i = 0; i < portfolio.Orders.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var o = portfolio.Orders[i];
                    sb.AppendFormat("{{\"OrderId\":\"{0}\",\"Instrument\":\"{1}\",\"Side\":{2},\"State\":{3},\"Type\":{4},\"Price\":{5},\"Quantity\":{6},\"FilledQuantity\":{7},\"AverageFillPrice\":{8}}}", Escape(o.OrderId), Escape(o.Instrument), o.Side, o.State, o.Type, o.Price, o.Quantity, o.FilledQuantity, o.AverageFillPrice);
                }
                sb.Append(']');
                sb.Append(",\"Positions\":[");
                for (int i = 0; i < portfolio.Positions.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var p = portfolio.Positions[i];
                    sb.AppendFormat("{{\"Instrument\":\"{0}\",\"Quantity\":{1},\"AveragePrice\":{2},\"UnrealizedPnL\":{3},\"RealizedPnL\":{4}}}", Escape(p.Instrument), p.Quantity, p.AveragePrice, p.UnrealizedPnL, p.RealizedPnL);
                }
                sb.Append(']');
                sb.AppendFormat(",\"RequestId\":\"{0}\"}}", Escape(portfolio.RequestId));
                return sb.ToString();
            }
            return "{}";
        }

        private ControlMessage DeserializeControlMessage(string json)
        {
            var msg = new ControlMessage();
            // Patterns
            const string typePattern = "\\\"Type\\\"\\s*:\\s*(\\d+)";
            const string instrumentPattern = "\\\"InstrumentName\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"";
            const string orderPattern = "\\\"OrderCommand\\\"\\s*:\\s*\\{([^}]*)\\}";
            var typeMatch = Regex.Match(json, typePattern);
            if (typeMatch.Success) msg.Type = (ControlMessage.RequestType)int.Parse(typeMatch.Groups[1].Value);
            var instMatch = Regex.Match(json, instrumentPattern);
            if (instMatch.Success) msg.InstrumentName = Unescape(instMatch.Groups[1].Value);
            var orderMatch = Regex.Match(json, orderPattern);
            if (orderMatch.Success)
            {
                var body = orderMatch.Groups[1].Value;
                var oc = new OrderCommand();
                var act = Regex.Match(body, "\\\"Action\\\"\\s*:\\s*(\\d+)");
                if (act.Success) oc.Action = (OrderCommand.OrderAction)int.Parse(act.Groups[1].Value);
                var qty = Regex.Match(body, "\\\"Quantity\\\"\\s*:\\s*(\\d+)");
                if (qty.Success) oc.Quantity = int.Parse(qty.Groups[1].Value);
                var cid = Regex.Match(body, "\\\"ClientOrderId\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
                if (cid.Success) oc.ClientOrderId = Unescape(cid.Groups[1].Value);
                var lim = Regex.Match(body, "\\\"LimitPrice\\\"\\s*:\\s*([0-9.]+)");
                if (lim.Success) oc.LimitPrice = double.Parse(lim.Groups[1].Value);
                msg.OrderCommand = oc;
            }
            return msg;
        }

        private string Escape(string s) => string.IsNullOrEmpty(s) ? s : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        private string Unescape(string s) => string.IsNullOrEmpty(s) ? s : s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        public void Dispose() { }
    }

    public class ControlPipeClient : IDisposable
    {
        private readonly string _pipeName;
        private readonly NamedPipeClientStream _pipeClient;

        public ControlPipeClient(string pipeName)
        {
            _pipeName = pipeName;
            _pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        }

        public Task ConnectAsync(int timeoutMs = 5000) => _pipeClient.ConnectAsync(timeoutMs);

        // High-level API ----------------------------------------------------
        public async Task<OrderStatusMessage?> SubmitOrderAsync(string instrumentName, OrderCommand orderCommand)
        {
            var msg = new ControlMessage { Type = ControlMessage.RequestType.SubmitOrder, InstrumentName = instrumentName, OrderCommand = orderCommand };
            var resp = await SendRequestAsync(msg);
            if (resp is string raw) return DeserializeOrderStatus(raw);
            return null;
        }

        public async Task<TickerDictionaryResponse?> GetTickerDictionaryAsync()
        {
            var msg = new ControlMessage { Type = ControlMessage.RequestType.GetTickerDictionary };            
            var resp = await SendRequestAsync(msg);
            if (resp is string raw) return DeserializeTickerDictionary(raw);
            return null;
        }

        public async Task<PortfolioStateMessage?> RequestPortfolioStateAsync()
        {
            var msg = new ControlMessage { Type = ControlMessage.RequestType.RequestPortfolioState };
            var resp = await SendRequestAsync(msg);
            if (resp is string raw) return DeserializePortfolioState(raw);
            return null;
        }
        // ------------------------------------------------------------------

        public async Task<object> SendRequestAsync(ControlMessage message)
        {
            var json = SerializeControlMessage(message);
            var buffer = Encoding.UTF8.GetBytes(json);
            await _pipeClient.WriteAsync(buffer, 0, buffer.Length);
            await _pipeClient.FlushAsync();
            buffer = new byte[32768];
            int read = await _pipeClient.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0) return string.Empty;
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        private string SerializeControlMessage(ControlMessage message)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.AppendFormat("\"Type\":{0}", (int)message.Type);
            if (!string.IsNullOrEmpty(message.InstrumentName))
                sb.AppendFormat(",\"InstrumentName\":\"{0}\"", Escape(message.InstrumentName));
            if (message.OrderCommand != null)
            {
                sb.Append(",\"OrderCommand\":{");
                sb.AppendFormat("\"Action\":{0},", (int)message.OrderCommand.Action);
                sb.AppendFormat("\"Quantity\":{0},", message.OrderCommand.Quantity);
                sb.AppendFormat("\"ClientOrderId\":\"{0}\",", Escape(message.OrderCommand.ClientOrderId ?? Guid.NewGuid().ToString()));
                sb.AppendFormat("\"LimitPrice\":{0}", message.OrderCommand.LimitPrice);
                sb.Append('}');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private OrderStatusMessage DeserializeOrderStatus(string json)
        {
            var status = new OrderStatusMessage();
            var idMatch = Regex.Match(json, "\\\"ClientOrderId\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
            if (idMatch.Success) status.ClientOrderId = Unescape(idMatch.Groups[1].Value);
            var stMatch = Regex.Match(json, "\\\"Status\\\"\\s*:\\s*(\\d+)");
            if (stMatch.Success) status.Status = (OrderCommand.OrderStatus)int.Parse(stMatch.Groups[1].Value);
            var msgMatch = Regex.Match(json, "\\\"Message\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
            if (msgMatch.Success) status.Message = Unescape(msgMatch.Groups[1].Value);
            return status;
        }

        private TickerRegistrationResponse DeserializeTickerRegistration(string json)
        {
            var resp = new TickerRegistrationResponse();
            var idMatch = Regex.Match(json, "\\\"TickerId\\\"\\s*:\\s*(\\d+)");
            if (idMatch.Success) resp.TickerId = byte.Parse(idMatch.Groups[1].Value);
            return resp;
        }

        private TickerDictionaryResponse DeserializeTickerDictionary(string json)
        {
            var resp = new TickerDictionaryResponse { TickerDictionary = new Dictionary<byte, string>() };
            var dictMatch = Regex.Match(json, "\\\"TickerDictionary\\\"\\s*:\\s*\\{([^}]*)\\}");
            if (dictMatch.Success)
            {
                var content = dictMatch.Groups[1].Value;
                var matches = Regex.Matches(content, "\\\"(\\d+)\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
                foreach (Match m in matches) resp.TickerDictionary[byte.Parse(m.Groups[1].Value)] = Unescape(m.Groups[2].Value);
            }
            return resp;
        }

        private PortfolioStateMessage DeserializePortfolioState(string json)
        {
            var msg = new PortfolioStateMessage { Orders = new System.Collections.Generic.List<WorkingOrderMessage>(), Positions = new System.Collections.Generic.List<PositionMessage>() };
            var acctMatch = Regex.Match(json, "\\\"Account\\\"\\s*:\\s*\\{([^}]*)\\}");
            if (acctMatch.Success)
            {
                var acc = new AccountStateMessage();
                var body = acctMatch.Groups[1].Value;
                var name = Regex.Match(body, "\\\"AccountName\\\"\\s*:\\s*\\\"([^\\\"]*)\\\""); if (name.Success) acc.AccountName = Unescape(name.Groups[1].Value);
                msg.Account = acc;
            }
            // Orders parsing (minimal)
            var ordersArray = Regex.Match(json, "\\\"Orders\\\"\\s*:\\s*\\[([^]]*)\\]");
            if (ordersArray.Success)
            {
                var ordersContent = ordersArray.Groups[1].Value;
                var orderMatches = Regex.Matches(ordersContent, "\\{([^}]*)\\}");
                foreach (Match om in orderMatches)
                {
                    var obody = om.Groups[1].Value;
                    var ord = new WorkingOrderMessage();
                    var idm = Regex.Match(obody, "\\\"OrderId\\\"\\s*:\\s*\\\"([^\\\"]*)\\\""); if (idm.Success) ord.OrderId = Unescape(idm.Groups[1].Value);
                    msg.Orders.Add(ord);
                }
            }
            // Positions parsing (minimal)
            var posArray = Regex.Match(json, "\\\"Positions\\\"\\s*:\\s*\\[([^]]*)\\]");
            if (posArray.Success)
            {
                var posContent = posArray.Groups[1].Value;
                var posMatches = Regex.Matches(posContent, "\\{([^}]*)\\}");
                foreach (Match pm in posMatches)
                {
                    var pbody = pm.Groups[1].Value;
                    var pos = new PositionMessage();
                    var inst = Regex.Match(pbody, "\\\"Instrument\\\"\\s*:\\s*\\\"([^\\\"]*)\\\""); if (inst.Success) pos.Instrument = Unescape(inst.Groups[1].Value);
                    msg.Positions.Add(pos);
                }
            }
            return msg;
        }

        private string Escape(string s) => string.IsNullOrEmpty(s) ? s : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        private string Unescape(string s) => string.IsNullOrEmpty(s) ? s : s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        public void Dispose() => _pipeClient?.Dispose();
    }
}
