using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Text;
using System.Text.Json;

namespace HackathonBotPOC.Services
{
    public class CommandService
    {
        private readonly GraphServiceClient _graphClient;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public CommandService(GraphServiceClient graphClient, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _graphClient = graphClient;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        private struct CommandData
        {
            public string Type { get; set; }
            public string To { get; set; }
            public string Cc { get; set; }
            public string Content { get; set; }
        }

        public async Task<(bool Success, string Message)> ProcessCommandAsync(string command)
        {
            var commandData = ParseCommand(command);
            if (!commandData.HasValue) return (false, "Invalid command format");

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_configuration["OpenAiApiKey"]}");

            try
            {
                if (commandData.Value.Type == "email")
                {
                    string formalContent = await GenerateFormalContent("email", commandData.Value, httpClient);
                    var formalJson = JsonSerializer.Deserialize<JsonElement>(formalContent);
                    await CreateDraftEmailAsync(commandData.Value,
                        formalJson.GetProperty("subject").GetString(),
                        formalJson.GetProperty("body").GetString());
                    return (true, "Draft email created successfully");
                }
                else if (commandData.Value.Type == "message")
                {
                    string formalMessage = await GenerateFormalContent("message", commandData.Value, httpClient);
                    await SendTeamsMessageAsync(commandData.Value.To, formalMessage);
                    return (true, "Teams message sent successfully");
                }
                return (false, "Unsupported command type");
            }
            catch (Exception ex)
            {
                return (false, $"Error processing command: {ex.Message}");
            }
        }

        private CommandData? ParseCommand(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            text = text.ToLower().Trim();
            try
            {
                if (text.StartsWith("email"))
                {
                    text = text.Replace("email ", "");
                    string[] parts = text.Split(" cc ");
                    string toPart = parts[0].Trim();
                    string ccPart = parts.Length > 1 ? parts[1].Split(" about ")[0].Trim() : "";
                    string content = parts.Length > 1 ? parts[1].Split(" about ")[1].Trim() : parts[1].Trim();

                    return new CommandData
                    {
                        Type = "email",
                        To = toPart,
                        Cc = ccPart,
                        Content = content
                    };
                }
                else if (text.StartsWith("message"))
                {
                    text = text.Replace("message ", "");
                    string[] parts = text.Split(" about ");
                    string toPart = parts[0].Trim();
                    string content = parts[1].Trim();

                    return new CommandData
                    {
                        Type = "message",
                        To = toPart,
                        Content = content
                    };
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing command: {ex.Message}");
                return null;
            }
        }

        private async Task<string> GenerateFormalContent(string type, CommandData commandData, HttpClient httpClient)
        {
            var prompt = type == "email"
                ? $"Convert this informal email request into a formal email:\nTo: {commandData.To}\nCC: {commandData.Cc}\nContent: {commandData.Content}\nProvide the response in JSON format with 'subject' and 'body' fields."
                : $"Convert this informal message into a formal Teams message:\nTo: {commandData.To}\nContent: {commandData.Content}\nProvide the response as a plain text string.";

            var requestBody = new
            {
                model = "gpt-3.5-turbo",
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.7
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync();
            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseString);
            return jsonResponse.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }

        private async Task CreateDraftEmailAsync(CommandData commandData, string subject, string body)
        {
            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody { ContentType = BodyType.Text, Content = body },
                ToRecipients = new List<Recipient>
                {
                    new Recipient { EmailAddress = new EmailAddress { Address = $"{commandData.To.Replace(" ", ".")}@{_configuration["EmailDomain"]}" } }
                },
                CcRecipients = string.IsNullOrEmpty(commandData.Cc) ? null : new List<Recipient>
                {
                    new Recipient { EmailAddress = new EmailAddress { Address = $"{commandData.Cc.Replace(" ", ".")}@{_configuration["EmailDomain"]}" } }
                },
                IsDraft = true
            };

            await _graphClient.Me.Messages.PostAsync(message);
        }

        private async Task SendTeamsMessageAsync(string recipient, string message)
        {
            var chat = recipient.ToLower().Contains("group")
                ? await GetOrCreateGroupChat(recipient)
                : await GetOrCreateOneOnOneChat(recipient);

            var chatMessage = new ChatMessage
            {
                Body = new ItemBody { ContentType = BodyType.Text, Content = message }
            };

            await _graphClient.Chats[chat.Id].Messages.PostAsync(chatMessage);
        }

        private async Task<Chat> GetOrCreateOneOnOneChat(string recipient)
        {
            string email = $"{recipient.Replace(" ", ".")}@{_configuration["EmailDomain"]}";
            var user = await _graphClient.Users[email].GetAsync();
            var chats = await _graphClient.Me.Chats.GetAsync();
            var existingChat = chats.Value.FirstOrDefault(c => c.ChatType == ChatType.OneOnOne && c.Members.Any(m => ((AadUserConversationMember)m).UserId == user.Id));

            if (existingChat != null) return existingChat;

            var newChat = new Chat
            {
                ChatType = ChatType.OneOnOne,
                Members = new List<ConversationMember>
                {
                    new AadUserConversationMember { Roles = new List<string> { "owner" }, UserId = user.Id }
                }
            };
            return await _graphClient.Chats.PostAsync(newChat);
        }

        private async Task<Chat> GetOrCreateGroupChat(string groupName)
        {
            var chats = await _graphClient.Me.Chats.GetAsync();
            var existingChat = chats.Value.FirstOrDefault(c => c.ChatType == ChatType.Group && c.Topic == groupName);

            if (existingChat != null) return existingChat;

            var newChat = new Chat
            {
                ChatType = ChatType.Group,
                Topic = groupName,
                Members = new List<ConversationMember>
                {
                    new AadUserConversationMember { Roles = new List<string> { "owner" }, UserId = (await _graphClient.Me.GetAsync()).Id }
                }
            };
            return await _graphClient.Chats.PostAsync(newChat);
        }
    }
}
