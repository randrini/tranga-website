﻿using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tranga.Connectors;
//TODO Download covers: https://api.mangadex.org/docs/retrieving-covers/ https://api.mangadex.org/docs/swagger.html#/

public class MangaDex : Connector
{
    internal override string downloadLocation { get; }
    public override string name { get; }
    private DownloadClient _downloadClient = new (1500);

    public MangaDex(string downloadLocation)
    {
        name = "MangaDex.org";
        this.downloadLocation = downloadLocation;
    }

    public override Publication[] GetPublications(string publicationTitle = "")
    {
        const int limit = 100;
        string publicationsUrl = $"https://api.mangadex.org/manga?limit={limit}&title={publicationTitle}&offset=";
        int offset = 0;
        int total = int.MaxValue;
        HashSet<Publication> publications = new();
        while (offset < total)
        {
            offset += limit;
            DownloadClient.RequestResult requestResult = _downloadClient.MakeRequest(string.Concat(publicationsUrl, "0"));
            JsonObject? result = JsonSerializer.Deserialize<JsonObject>(requestResult.result);
            if (result is null)
                break;
            
            total = result["total"]!.GetValue<int>();
            JsonArray mangaInResult = result["data"]!.AsArray();
            foreach (JsonObject manga in mangaInResult)
            {
                JsonObject attributes = manga["attributes"].AsObject();
                
                string title = attributes["title"]!.AsObject().ContainsKey("en") && attributes["title"]!["en"] is not null
                    ? attributes["title"]!["en"]!.GetValue<string>()
                    : "";
                
                string? description = attributes["description"]!.AsObject().ContainsKey("en") && attributes["description"]!["en"] is not null
                    ? attributes["description"]!["en"]!.GetValue<string?>()
                    : null;

                JsonArray altTitlesObject = attributes["altTitles"]!.AsArray();
                string[,] altTitles = new string[altTitlesObject.Count, 2];
                int titleIndex = 0;
                foreach (JsonObject altTitleObject in altTitlesObject)
                {
                    string key = ((IDictionary<string, JsonNode?>)altTitleObject!).Keys.ToArray()[0];
                    altTitles[titleIndex, 0] = key;
                    altTitles[titleIndex++, 1] = altTitleObject[key]!.GetValue<string>();
                }

                JsonArray tagsObject = attributes["tags"]!.AsArray();
                HashSet<string> tags = new();
                foreach (JsonObject tagObject in tagsObject)
                {
                    if(tagObject!["attributes"]!["name"]!.AsObject().ContainsKey("en"))
                        tags.Add(tagObject!["attributes"]!["name"]!["en"]!.GetValue<string>());
                }

                string? poster = null;
                if (manga.ContainsKey("relationships") && manga["relationships"] is not null)
                {
                    JsonArray relationships = manga["relationships"]!.AsArray();
                    poster = relationships.FirstOrDefault(relationship => relationship!["type"]!.GetValue<string>() == "cover_art")!["id"]!.GetValue<string>();
                }

                string[,]? links = null;
                if (attributes.ContainsKey("links") && attributes["links"] is not null)
                {
                    JsonObject linksObject = attributes["links"]!.AsObject();
                    links = new string[linksObject.Count, 2];
                    int linkIndex = 0;
                    foreach (string key in ((IDictionary<string, JsonNode?>)linksObject).Keys)
                    {
                        links[linkIndex, 0] = key;
                        links[linkIndex++, 1] = linksObject[key]!.GetValue<string>();
                    }
                }
                
                int? year = attributes.ContainsKey("year") && attributes["year"] is not null
                    ? attributes["year"]!.GetValue<int?>()
                    : null;

                string? originalLanguage = attributes.ContainsKey("originalLanguage") && attributes["originalLanguage"] is not null
                    ? attributes["originalLanguage"]!.GetValue<string?>()
                    : null;
                
                string status = attributes["status"]!.GetValue<string>();

                Publication pub = new Publication(
                    title,
                    description,
                    altTitles,
                    tags.ToArray(),
                    poster,
                    links,
                    year,
                    originalLanguage,
                    status,
                    this,
                    manga["id"]!.GetValue<string>()
                );
                publications.Add(pub);
            }
        }

        return publications.ToArray();
    }

    public override Chapter[] GetChapters(Publication publication)
    {
        const int limit = 100;
        int offset = 0;
        string id = publication.downloadUrl;
        int total = int.MaxValue;
        List<Chapter> chapters = new();
        while (offset < total)
        {
            offset += limit;
            DownloadClient.RequestResult requestResult =
                _downloadClient.MakeRequest($"https://api.mangadex.org/manga/{id}/feed?limit={limit}&offset={offset}");
            JsonObject? result = JsonSerializer.Deserialize<JsonObject>(requestResult.result);
            if (result is null)
                break;
            
            total = result["total"]!.GetValue<int>();
            JsonArray chaptersInResult = result["data"]!.AsArray();
            foreach (JsonObject chapter in chaptersInResult)
            {
                JsonObject attributes = chapter!["attributes"]!.AsObject();
                string chapterId = chapter["id"]!.GetValue<string>();
                
                string? title = attributes.ContainsKey("title") && attributes["title"] is not null
                    ? attributes["title"]!.GetValue<string>()
                    : null;
                
                string? volume = attributes.ContainsKey("volume") && attributes["volume"] is not null
                    ? attributes["volume"]!.GetValue<string>()
                    : null;
                
                string? chapterNum = attributes.ContainsKey("chapter") && attributes["chapter"] is not null
                    ? attributes["chapter"]!.GetValue<string>()
                    : null;
                
                chapters.Add(new Chapter(publication, title, volume, chapterNum, chapterId));
            }
        }
        return chapters.OrderBy(chapter => chapter.chapterNumber).ToArray();
    }

    public override void DownloadChapter(Publication publication, Chapter chapter)
    {
        DownloadClient.RequestResult requestResult =
            _downloadClient.MakeRequest($"https://api.mangadex.org/at-home/server/{chapter.url}?forcePort443=false'");
        JsonObject? result = JsonSerializer.Deserialize<JsonObject>(requestResult.result);
        if (result is null)
            return;

        string baseUrl = result["baseUrl"]!.GetValue<string>();
        string hash = result["chapter"]!["hash"]!.GetValue<string>();
        JsonArray imageFileNames = result["chapter"]!["data"]!.AsArray();
        HashSet<string> imageUrls = new();
        foreach (JsonNode image in imageFileNames)
            imageUrls.Add($"{baseUrl}/{hash}/{image!.GetValue<string>()}");

        string fileName = string.Concat(publication.sortName.Split(Path.GetInvalidFileNameChars()));

        DownloadChapter(imageUrls.ToArray(), Path.Join(downloadLocation, fileName));
    }

    internal override void DownloadImage(string url, string path)
    {
        DownloadClient.RequestResult requestResult = _downloadClient.MakeRequest(url);
        byte[] buffer = new byte[requestResult.result.Length];
        requestResult.result.ReadExactly(buffer, 0, buffer.Length);
        File.WriteAllBytes(path, buffer);
    }
}