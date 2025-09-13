using System;
using System.Collections.Generic;
using System.Globalization;
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
                    sb.AppendFormat(CultureInfo.InvariantCulture, "\"{0}\":\"{1}\"", kvp.Key, Escape(kvp.Value));
                    first = false;
                }
                sb.Append('}');
                sb.AppendFormat(CultureInfo.InvariantCulture, ",\"Timestamp\":\"{0:o}\"", tickerDict.Timestamp);
                sb.AppendFormat(CultureInfo.InvariantCulture, ",\"RequestId\":\"{0}\"", Escape(tickerDict.RequestId ?? string.Empty));
                sb.Append('}');
                return sb.ToString();
            }
            if (response is TickerRegistrationResponse reg)
            {
                return string.Format(CultureInfo.InvariantCulture, "{{\"TickerId\":{0},\"Success\":{1},\"Message\":\"{2}\",\"Timestamp\":\"{3:o}\",\"RequestId\":\"{4}\"}}",
                    reg.TickerId, reg.Success ? "true" : "false", Escape(reg.Message), reg.Timestamp, Escape(reg.RequestId));
            }
            if (response is OrderStatusMessage order)
            {
                return string.Format(CultureInfo.InvariantCulture, "{{\"ClientOrderId\":\"{0}\",\"Status\":{1},\"Message\":\"{2}\",\"FillPrice\":{3},\"FillQuantity\":{4},\"Timestamp\":\"{5:o}\"}}",
                    Escape(order.ClientOrderId), (int)order.Status, Escape(order.Message), order.FillPrice, order.FillQuantity, order.Timestamp);
            }
            if (response is PortfolioStateMessage portfolio)
            {
                var sb = new StringBuilder();
                sb.Append('{');
                sb.AppendFormat(CultureInfo.InvariantCulture, "\"Timestamp\":\"{0:o}\"", portfolio.Timestamp);
                sb.Append(',');
                if (portfolio.Account != null)
                {
                    sb.Append("\"Account\":{");
                    sb.AppendFormat(CultureInfo.InvariantCulture, "\"AccountName\":\"{0}\"", Escape(portfolio.Account.AccountName));
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"BuyingPower\":{0}", portfolio.Account.BuyingPower);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"CashValue\":{0}", portfolio.Account.CashValue);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"UnrealizedPnL\":{0}", portfolio.Account.UnrealizedPnL);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"RealizedPnL\":{0}", portfolio.Account.RealizedPnL);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"NetLiquidation\":{0}", portfolio.Account.NetLiquidation);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"InitialMargin\":{0}", portfolio.Account.InitialMargin);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"MaintenanceMargin\":{0}", portfolio.Account.MaintenanceMargin);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"ExcessEquity\":{0}", portfolio.Account.ExcessEquity);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"Commission\":{0}}}", portfolio.Account.Commission);
                }
                else sb.Append("\"Account\":{}");
                // Orders
                sb.Append(",\"Orders\":[");
                for (int i = 0; i < portfolio.Orders.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var o = portfolio.Orders[i];
                    sb.Append('{');
                    sb.AppendFormat(CultureInfo.InvariantCulture, "\"OrderId\":\"{0}\"", Escape(o.OrderId));
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"Instrument\":\"{0}\"", Escape(o.Instrument));
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"Side\":{0}", o.Side);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"State\":{0}", o.State);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"Type\":{0}", o.Type);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"TimeInForce\":{0}", o.TimeInForce);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"Price\":{0}", o.Price);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"Quantity\":{0}", o.Quantity);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"FilledQuantity\":{0}", o.FilledQuantity);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"AverageFillPrice\":{0}", o.AverageFillPrice);
                    sb.Append('}');
                }
                sb.Append(']');
                // Positions
                sb.Append(",\"Positions\":[");
                for (int i = 0; i < portfolio.Positions.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var p = portfolio.Positions[i];
                    sb.Append('{');
                    sb.AppendFormat(CultureInfo.InvariantCulture, "\"Instrument\":\"{0}\"", Escape(p.Instrument));
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"Quantity\":{0}", p.Quantity);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"AveragePrice\":{0}", p.AveragePrice);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"UnrealizedPnL\":{0}", p.UnrealizedPnL);
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"RealizedPnL\":{0}", p.RealizedPnL);
                    sb.Append('}');
                }
                sb.Append(']');
                sb.AppendFormat(CultureInfo.InvariantCulture, ",\"RequestId\":\"{0}\"}}", Escape(portfolio.RequestId));
                return sb.ToString();
            }
            return "{}";
        }

        private ControlMessage DeserializeControlMessage(string json)
        {
            var msg = new ControlMessage();
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
                if (lim.Success) oc.LimitPrice = double.Parse(lim.Groups[1].Value, CultureInfo.InvariantCulture);
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
                sb.AppendFormat("\"LimitPrice\":{0}", message.OrderCommand.LimitPrice.ToString(CultureInfo.InvariantCulture));
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
            if (idMatch.Success) resp.TickerId = byte.Parse(idMatch.Groups[1].Value, CultureInfo.InvariantCulture);
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
                foreach (Match m in matches) resp.TickerDictionary[byte.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = Unescape(m.Groups[2].Value);
            }
            return resp;
        }

        private PortfolioStateMessage DeserializePortfolioState(string json)
        {
            var msg = new PortfolioStateMessage
            {
                Orders = new System.Collections.Generic.List<WorkingOrderMessage>(),
                Positions = new System.Collections.Generic.List<PositionMessage>()
            };

            var acctMatch = Regex.Match(json, @"""Account""\s*:\s*\{([^}]*)\}", RegexOptions.CultureInvariant | RegexOptions.Singleline);
            if (acctMatch.Success)
            {
                var body = acctMatch.Groups[1].Value;
                var acc = new AccountStateMessage();
                var name = Regex.Match(body, @"""AccountName""\s*:\s*""([^""]*)""", RegexOptions.CultureInvariant);
                if (name.Success) acc.AccountName = Unescape(name.Groups[1].Value);
                msg.Account = acc;
            }

            var ordersArray = Regex.Match(json, @"""Orders""\s*:\s*\[(.*?)\]", RegexOptions.CultureInvariant | RegexOptions.Singleline);
            if (ordersArray.Success)
            {
                var ordersContent = ordersArray.Groups[1].Value;
                var orderMatches = Regex.Matches(ordersContent, @"\{(.*?)\}", RegexOptions.CultureInvariant | RegexOptions.Singleline);
                foreach (Match om in orderMatches)
                {
                    var obody = om.Groups[1].Value;
                    var ord = new WorkingOrderMessage();
                    var idm = Regex.Match(obody, @"""OrderId""\s*:\s*""([^""]*)""", RegexOptions.CultureInvariant); if (idm.Success) ord.OrderId = Unescape(idm.Groups[1].Value);
                    var inst = Regex.Match(obody, @"""Instrument""\s*:\s*""([^""]*)""", RegexOptions.CultureInvariant); if (inst.Success) ord.Instrument = Unescape(inst.Groups[1].Value);
                    var side = Regex.Match(obody, @"""Side""\s*:\s*(\d+)", RegexOptions.CultureInvariant); if (side.Success) ord.Side = byte.Parse(side.Groups[1].Value, CultureInfo.InvariantCulture);
                    var tif = Regex.Match(obody, @"""TimeInForce""\s*:\s*(\d+)", RegexOptions.CultureInvariant); if (tif.Success) ord.TimeInForce = byte.Parse(tif.Groups[1].Value, CultureInfo.InvariantCulture);
                    var state = Regex.Match(obody, @"""State""\s*:\s*(\d+)", RegexOptions.CultureInvariant); if (state.Success) ord.State = byte.Parse(state.Groups[1].Value, CultureInfo.InvariantCulture);
                    var type = Regex.Match(obody, @"""Type""\s*:\s*(\d+)", RegexOptions.CultureInvariant); if (type.Success) ord.Type = byte.Parse(type.Groups[1].Value, CultureInfo.InvariantCulture);
                    var price = Regex.Match(obody, @"""Price""\s*:\s*(-?[0-9.]+)", RegexOptions.CultureInvariant); if (price.Success) ord.Price = double.Parse(price.Groups[1].Value, CultureInfo.InvariantCulture);
                    var qty = Regex.Match(obody, @"""Quantity""\s*:\s*(-?\d+)", RegexOptions.CultureInvariant); if (qty.Success) ord.Quantity = int.Parse(qty.Groups[1].Value, CultureInfo.InvariantCulture);
                    var filled = Regex.Match(obody, @"""FilledQuantity""\s*:\s*(-?\d+)", RegexOptions.CultureInvariant); if (filled.Success) ord.FilledQuantity = int.Parse(filled.Groups[1].Value, CultureInfo.InvariantCulture);
                    var avg = Regex.Match(obody, @"""AverageFillPrice""\s*:\s*(-?[0-9.]+)", RegexOptions.CultureInvariant); if (avg.Success) ord.AverageFillPrice = double.Parse(avg.Groups[1].Value, CultureInfo.InvariantCulture);
                    msg.Orders.Add(ord);
                }
            }

            var posArray = Regex.Match(json, @"""Positions""\s*:\s*\[(.*?)\]", RegexOptions.CultureInvariant | RegexOptions.Singleline);
            if (posArray.Success)
            {
                var posContent = posArray.Groups[1].Value;
                var posMatches = Regex.Matches(posContent, @"\{(.*?)\}", RegexOptions.CultureInvariant | RegexOptions.Singleline);
                foreach (Match pm in posMatches)
                {
                    var pbody = pm.Groups[1].Value;
                    var pos = new PositionMessage();
                    var inst = Regex.Match(pbody, @"""Instrument""\s*:\s*""([^""]*)""", RegexOptions.CultureInvariant); if (inst.Success) pos.Instrument = Unescape(inst.Groups[1].Value);
                    var qty = Regex.Match(pbody, @"""Quantity""\s*:\s*(-?\d+)", RegexOptions.CultureInvariant); if (qty.Success) pos.Quantity = int.Parse(qty.Groups[1].Value, CultureInfo.InvariantCulture);
                    var avgPx = Regex.Match(pbody, @"""AveragePrice""\s*:\s*(-?[0-9.]+)", RegexOptions.CultureInvariant); if (avgPx.Success) pos.AveragePrice = double.Parse(avgPx.Groups[1].Value, CultureInfo.InvariantCulture);
                    var pnl = Regex.Match(pbody, @"""UnrealizedPnL""\s*:\s*(-?[0-9.]+)", RegexOptions.CultureInvariant); if (pnl.Success) pos.UnrealizedPnL = double.Parse(pnl.Groups[1].Value, CultureInfo.InvariantCulture);
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
