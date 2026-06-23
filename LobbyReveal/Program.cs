using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LobbyReveal
{
    class Program
    {
        static async Task Main(string[] args)
        {

            string lockfilePath = "";

            Process[] lolProcesses = Process.GetProcessesByName("LeagueClientUx");

            if (lolProcesses.Length > 0)
            {
                try
                {
                    string exePath = lolProcesses[0].MainModule.FileName;
                    string lolDirectory = Path.GetDirectoryName(exePath);
                    lockfilePath = Path.Combine(lolDirectory, "lockfile");
                }
                catch (Exception)
                {
                    Console.WriteLine("Permission error: Try opening this program as an administrator.");
                    Console.ReadLine();
                    return;
                }
            }

            if (string.IsNullOrEmpty(lockfilePath) || !File.Exists(lockfilePath))
            {
                Console.WriteLine("Lockfile wasn't found. Is League open?.");
                Console.ReadLine();
                return;
            }

            string lockfileContent;

            try
            {
                using (var fileStream = new FileStream(lockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var streamReader = new StreamReader(fileStream))
                {
                    lockfileContent = streamReader.ReadToEnd();
                }
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine("Lockfile wasn't found. Is League open?");
                Console.ReadLine();
                return;
            }

            string[] lockfileData = lockfileContent.Split(':');
            string port = lockfileData[2];
            string password = lockfileData[3];
            string protocol = lockfileData[4];

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true
            };

            using var lcuClient = new HttpClient(handler);
            lcuClient.BaseAddress = new Uri($"{protocol}://127.0.0.1:{port}");

            var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{password}"));
            lcuClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);

            using var opggClient = new HttpClient();

            while (true)
            {
                Console.WriteLine("\nPress Enter in Champ Select.");
                Console.ReadLine();

                try
                {
                    var sessionResponse = await lcuClient.GetAsync("/lol-champ-select/v1/session");
                    if (!sessionResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"The lobby hasn't loaded yet, or you aren't in Champ Select. (Status: {sessionResponse.StatusCode}).");
                        continue;
                    }

                    var sessionJson = await sessionResponse.Content.ReadAsStringAsync();
                    var sessionNode = JsonNode.Parse(sessionJson);
                    var myTeam = sessionNode?["myTeam"]?.AsArray();

                    if (myTeam == null || myTeam.Count == 0)
                    {
                        Console.WriteLine("There's no players on your team yet");
                        continue;
                    }

                    Console.WriteLine("\n--- Lobby Reveal ---");

                    foreach (var member in myTeam)
                    {
                        string summonerId = member["summonerId"]?.ToString();

                        if (!string.IsNullOrEmpty(summonerId) && summonerId != "0")
                        {
                            var sumResponse = await lcuClient.GetAsync($"/lol-summoner/v1/summoners/{summonerId}");
                            if (sumResponse.IsSuccessStatusCode)
                            {
                                var sumJson = await sumResponse.Content.ReadAsStringAsync();
                                var sumNode = JsonNode.Parse(sumJson);

                                string gameName = sumNode?["gameName"]?.ToString();
                                string tagLine = sumNode?["tagLine"]?.ToString();

                                if (string.IsNullOrEmpty(gameName) && sumNode?["displayName"] != null)
                                {
                                    var parts = sumNode["displayName"].ToString().Split('#');
                                    gameName = parts[0];
                                    tagLine = parts.Length > 1 ? parts[1] : "BR1";
                                }

                                if (!string.IsNullOrEmpty(gameName))
                                {
                                    await FetchAndPrintOpggStats(opggClient, gameName, tagLine, "br");
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("Anonymous Player.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Critical execution error: {ex.Message}");
                }
            }
        }

        static async Task FetchAndPrintOpggStats(HttpClient client, string gameName, string tagLine, string region)
        {
            try
            {
                var mcpPayload = new
                {
                    jsonrpc = "2.0",
                    id = Guid.NewGuid().ToString(),
                    method = "tools/call",
                    @params = new
                    {
                        name = "lol_get_summoner_profile",
                        arguments = new
                        {
                            region = region,
                            game_name = gameName,
                            tag_line = tagLine
                        }
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(mcpPayload), Encoding.UTF8, "application/json");

                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                var response = await client.PostAsync("https://mcp-api.op.gg/mcp", content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return;

                var responseNode = JsonNode.Parse(responseString);
                var toolTextResult = responseNode?["result"]?["content"]?[0]?["text"]?.ToString();

                if (!string.IsNullOrEmpty(toolTextResult))
                {
                    try
                    {
                        var opggData = JsonNode.Parse(toolTextResult);
                        var statsArray = opggData?["data"]?["summoner"]?["league_stats"]?.AsArray()
                                      ?? opggData?["data"]?["league_stats"]?.AsArray()
                                      ?? opggData?["league_stats"]?.AsArray();

                        if (statsArray != null && statsArray.Count > 0)
                        {
                            var targetQueue = statsArray.FirstOrDefault(x => x["game_type"]?.ToString().ToUpper().Contains("SOLO") == true) ?? statsArray[0];
                            string tier = targetQueue["tier_info"]?["tier"]?.ToString() ?? "UNRANKED";
                            string division = targetQueue["tier_info"]?["division"]?.ToString() ?? "";

                            int.TryParse(targetQueue["win"]?.ToString(), out int wins);
                            int.TryParse(targetQueue["lose"]?.ToString(), out int losses);

                            PrintStats(gameName, tagLine, tier, division, wins, losses);
                        }
                        else
                        {
                            Console.WriteLine($"{gameName} #{tagLine} - UNRANKED");
                        }
                    }
                    catch (JsonException)
                    {
                        string pattern = @"LeagueStat\(""SOLORANKED"",TierInfo\(([^,]+),([^,]+),.*?\),([^,]+),([^,]+)";
                        var match = Regex.Match(toolTextResult, pattern);

                        if (match.Success)
                        {
                            string tier = match.Groups[1].Value.Replace("\"", "").Trim();
                            string division = match.Groups[2].Value.Replace("\"", "").Trim();

                            int.TryParse(match.Groups[3].Value.Trim(), out int wins);
                            int.TryParse(match.Groups[4].Value.Trim(), out int losses);

                            PrintStats(gameName, tagLine, tier, division, wins, losses);
                        }
                        else
                        {
                            Console.WriteLine($"{gameName} #{tagLine} - UNRANKED");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"{gameName} #{tagLine} - UNRANKED");
                }
            }
            catch
            {
                Console.WriteLine($"{gameName} #{tagLine} - UNRANKED");
            }
        }

        static void PrintStats(string gameName, string tagLine, string tier, string division, int wins, int losses)
        {
            int totalGames = wins + losses;
            int wr = totalGames > 0 ? (int)Math.Round((double)wins / totalGames * 100) : 0;

            if (tier == "null" || tier.ToUpper() == "UNRANKED")
            {
                if (totalGames == 0)
                    Console.WriteLine($"{gameName} #{tagLine} - UNRANKED");
                else
                    Console.WriteLine($"{gameName} #{tagLine} - UNRANKED ({wins}W - {losses}L [{wr}% WR])");
            }
            else
            {
                string rankDisplay = (string.IsNullOrEmpty(division) || division == "null") ? tier.ToUpper() : $"{tier.ToUpper()} {division}";
                Console.WriteLine($"{gameName} #{tagLine} - {rankDisplay} ({wins}W - {losses}L [{wr}% WR])");
            }
        }
    }
}