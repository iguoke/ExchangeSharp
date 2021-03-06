﻿/*
MIT LICENSE

Copyright 2017 Digital Ruby, LLC - http://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExchangeSharp
{
    public sealed class ExchangeGeminiAPI : ExchangeAPI
    {
        public override string BaseUrl { get; set; } = "https://api.gemini.com/v1";
        public override string Name => ExchangeName.Gemini;

        public ExchangeGeminiAPI()
        {
        }

        private ExchangeVolume ParseVolume(JToken token)
        {
            ExchangeVolume vol = new ExchangeVolume();
            JProperty[] props = token.Children<JProperty>().ToArray();
            if (props.Length == 3)
            {
                vol.BaseSymbol = props[0].Name;
                vol.BaseVolume = props[0].Value.ConvertInvariant<decimal>();
                vol.ConvertedSymbol = props[1].Name;
                vol.ConvertedVolume = props[1].Value.ConvertInvariant<decimal>();
                vol.Timestamp = CryptoUtility.UnixTimeStampToDateTimeMilliseconds(props[2].Value.ConvertInvariant<long>());
            }

            return vol;
        }

        private ExchangeOrderResult ParseOrder(JToken result)
        {
            decimal amount = result["original_amount"].ConvertInvariant<decimal>();
            decimal amountFilled = result["executed_amount"].ConvertInvariant<decimal>();
            return new ExchangeOrderResult
            {
                Amount = amount,
                AmountFilled = amountFilled,
                Price = result["price"].ConvertInvariant<decimal>(),
                AveragePrice = result["avg_execution_price"].ConvertInvariant<decimal>(),
                Message = string.Empty,
                OrderId = result["id"].ToStringInvariant(),
                Result = (amountFilled == amount ? ExchangeAPIOrderResult.Filled : (amountFilled == 0 ? ExchangeAPIOrderResult.Pending : ExchangeAPIOrderResult.FilledPartially)),
                OrderDate = CryptoUtility.UnixTimeStampToDateTimeMilliseconds(result["timestampms"].ConvertInvariant<double>()),
                Symbol = result["symbol"].ToStringInvariant(),
                IsBuy = result["side"].ToStringInvariant() == "buy"
            };
        }

        private void CheckError(JToken result)
        {
            if (result != null && !(result is JArray) && result["result"] != null && result["result"].ToStringInvariant() == "error")
            {
                throw new APIException(result["reason"].ToStringInvariant());
            }
        }

        protected override void ProcessRequest(HttpWebRequest request, Dictionary<string, object> payload)
        {
            if (CanMakeAuthenticatedRequest(payload))
            {
                payload.Add("request", request.RequestUri.AbsolutePath);
                string json = JsonConvert.SerializeObject(payload);
                string json64 = System.Convert.ToBase64String(Encoding.ASCII.GetBytes(json));
                string hexSha384 = CryptoUtility.SHA384Sign(json64, CryptoUtility.SecureStringToString(PrivateApiKey));
                request.Headers["X-GEMINI-PAYLOAD"] = json64;
                request.Headers["X-GEMINI-SIGNATURE"] = hexSha384;
                request.Headers["X-GEMINI-APIKEY"] = CryptoUtility.SecureStringToString(PublicApiKey);
                request.Method = "POST";

                // gemini doesn't put the payload in the post body it puts it in as a http header, so no need to write to request stream
            }
        }

        public override string NormalizeSymbol(string symbol)
        {
            return (symbol ?? string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        }

        protected override async Task<IEnumerable<string>> OnGetSymbolsAsync()
        {
            return await MakeJsonRequestAsync<string[]>("/symbols");
        }

        protected override async Task<ExchangeTicker> OnGetTickerAsync(string symbol)
        {
            symbol = NormalizeSymbol(symbol);
            JObject obj = await MakeJsonRequestAsync<Newtonsoft.Json.Linq.JObject>("/pubticker/" + symbol);
            if (obj == null || obj.Count == 0)
            {
                return null;
            }
            ExchangeTicker t = new ExchangeTicker
            {
                Ask = obj["ask"].ConvertInvariant<decimal>(),
                Bid = obj["bid"].ConvertInvariant<decimal>(),
                Last = obj["last"].ConvertInvariant<decimal>()
            };
            t.Volume = ParseVolume(obj["volume"]);
            return t;
        }

        protected override async Task<ExchangeOrderBook> OnGetOrderBookAsync(string symbol, int maxCount = 100)
        {
            symbol = NormalizeSymbol(symbol);
            JObject obj = await MakeJsonRequestAsync<Newtonsoft.Json.Linq.JObject>("/book/" + symbol + "?limit_bids=" + maxCount + "&limit_asks=" + maxCount);
            if (obj == null || obj.Count == 0)
            {
                return null;
            }

            ExchangeOrderBook orders = new ExchangeOrderBook();
            JToken bids = obj["bids"];
            foreach (JToken token in bids)
            {
                ExchangeOrderPrice order = new ExchangeOrderPrice { Amount = token["amount"].ConvertInvariant<decimal>(), Price = token["price"].ConvertInvariant<decimal>() };
                orders.Bids.Add(order);
            }
            JToken asks = obj["asks"];
            foreach (JToken token in asks)
            {
                ExchangeOrderPrice order = new ExchangeOrderPrice { Amount = token["amount"].ConvertInvariant<decimal>(), Price = token["price"].ConvertInvariant<decimal>() };
                orders.Asks.Add(order);
            }
            return orders;
        }

        protected override async Task OnGetHistoricalTradesAsync(System.Func<IEnumerable<ExchangeTrade>, bool> callback, string symbol, DateTime? sinceDateTime = null)
        {
            const int maxCount = 100;
            symbol = NormalizeSymbol(symbol);
            string baseUrl = "/trades/" + symbol + "?limit_trades=" + maxCount;
            string url;
            List<ExchangeTrade> trades = new List<ExchangeTrade>();
            while (true)
            {
                url = baseUrl;
                if (sinceDateTime != null)
                {
                    url += "&timestamp=" + CryptoUtility.UnixTimestampFromDateTimeMilliseconds(sinceDateTime.Value).ToStringInvariant();
                }
                JArray obj = await MakeJsonRequestAsync<Newtonsoft.Json.Linq.JArray>(url);
                if (obj == null || obj.Count == 0)
                {
                    break;
                }
                if (sinceDateTime != null)
                {
                    sinceDateTime = CryptoUtility.UnixTimeStampToDateTimeMilliseconds(obj.First["timestampms"].ConvertInvariant<long>());
                }
                foreach (JToken token in obj)
                {
                    trades.Add(new ExchangeTrade
                    {
                        Amount = token["amount"].ConvertInvariant<decimal>(),
                        Price = token["price"].ConvertInvariant<decimal>(),
                        Timestamp = CryptoUtility.UnixTimeStampToDateTimeMilliseconds(token["timestampms"].ConvertInvariant<long>()),
                        Id = token["tid"].ConvertInvariant<long>(),
                        IsBuy = token["type"].ToStringInvariant() == "buy"
                    });
                }
                trades.Sort((t1, t2) => t1.Timestamp.CompareTo(t2.Timestamp));
                if (!callback(trades))
                {
                    break;
                }
                trades.Clear();
                if (obj.Count < maxCount || sinceDateTime == null)
                {
                    break;
                }
                Task.Delay(1000).Wait();
            }
        }

        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAsync()
        {
            Dictionary<string, decimal> lookup = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            JArray obj = await MakeJsonRequestAsync<Newtonsoft.Json.Linq.JArray>("/balances", null, GetNoncePayload());
            CheckError(obj);
            var q = from JToken token in obj
                    select new { Currency = token["currency"].ToStringInvariant(), Available = token["amount"].ConvertInvariant<decimal>() };
            foreach (var kv in q)
            {
                if (kv.Available > 0m)
                {
                    lookup[kv.Currency] = kv.Available;
                }
            }
            return lookup;
        }

        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAvailableToTradeAsync()
        {
            Dictionary<string, decimal> lookup = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            JArray obj = await MakeJsonRequestAsync<Newtonsoft.Json.Linq.JArray>("/balances", null, GetNoncePayload());
            CheckError(obj);
            var q = from JToken token in obj
                    select new { Currency = token["currency"].ToStringInvariant(), Available = token["available"].ConvertInvariant<decimal>() };
            foreach (var kv in q)
            {
                if (kv.Available > 0m)
                {
                    lookup[kv.Currency] = kv.Available;
                }
            }
            return lookup;
        }

        protected override async Task<ExchangeOrderResult> OnPlaceOrderAsync(ExchangeOrderRequest order)
        {
            if (order.OrderType == OrderType.Market)
            {
                throw new NotSupportedException("Order type " + order.OrderType + " not supported");
            }

            string symbol = NormalizeSymbol(order.Symbol);
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "nonce", GenerateNonce() },
                { "client_order_id", "ExchangeSharp_" + DateTime.UtcNow.ToString("s", System.Globalization.CultureInfo.InvariantCulture) },
                { "symbol", symbol },
                { "amount", order.RoundAmount().ToStringInvariant() },
                { "price", order.Price.ToStringInvariant() },
                { "side", (order.IsBuy ? "buy" : "sell") },
                { "type", "exchange limit" }
            };
            foreach (var kv in order.ExtraParameters)
            {
                payload[kv.Key] = kv.Value;
            }

            JToken obj = await MakeJsonRequestAsync<JToken>("/order/new", null, payload);
            CheckError(obj);
            return ParseOrder(obj);
        }

        protected override async Task<ExchangeOrderResult> OnGetOrderDetailsAsync(string orderId, string symbol = null)
        {
            if (string.IsNullOrWhiteSpace(orderId))
            {
                return null;
            }

            JToken result = await MakeJsonRequestAsync<JToken>("/order/status", null, new Dictionary<string, object> { { "nonce", GenerateNonce() }, { "order_id", orderId } });
            CheckError(result);
            return ParseOrder(result);
        }

        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string symbol = null)
        {
            List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
            symbol = NormalizeSymbol(symbol);
            JToken result = await MakeJsonRequestAsync<JToken>("/orders", null, new Dictionary<string, object> { { "nonce", GenerateNonce() } });
            CheckError(result);
            if (result is JArray array)
            {
                foreach (JToken token in array)
                {
                    if (symbol == null || token["symbol"].ToStringInvariant() == symbol)
                    {
                        orders.Add(ParseOrder(token));
                    }
                }
            }

            return orders;
        }

        protected override async Task OnCancelOrderAsync(string orderId, string symbol = null)
        {
            JObject result = await MakeJsonRequestAsync<JObject>("/order/cancel", null, new Dictionary<string, object>{ { "nonce", GenerateNonce() }, { "order_id", orderId } });
            CheckError(result);
        }
    }
}
