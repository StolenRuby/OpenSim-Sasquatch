using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using System.Collections.Specialized;
using System.Net;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AgentInventoryServiceModule")]
    public class AgentInventoryServiceModule : ISharedRegionModule
    {
        public bool Enabled { get; private set; }

        private int NumberOfScenes;

        private IInventoryService m_inventoryService = null;
        private string m_inventoryAPIv3Url;

        Dictionary<string, CategoryHandler> CategoryMethodHandlers = new Dictionary<string, CategoryHandler>();
        Dictionary<string, ItemHandler> ItemMethodHandlers = new Dictionary<string, ItemHandler>();

        #region Types

        delegate void CategoryHandler(IOSHttpResponse httpResponse, CategoryRequestData request);
        delegate void ItemHandler(IOSHttpResponse httpResponse, ItemRequestData request);

        public class RequestData
        {
            public UUID AgentID;
            public string Method;
            public OSDMap Map;

            public bool Simulate;
            public UUID TransactionID;
        }

        public class CategoryRequestData : RequestData
        {
            public InventoryFolderBase Folder;
            public string ContentType;
            public int Depth;
        }

        public class ItemRequestData : RequestData
        {
            public InventoryItemBase Item;
        }

        #endregion

        Dictionary<string, FolderType> NamedFolders = new Dictionary<string, FolderType>
        {
            {"animatn", FolderType.Animation },
            {"bodypart", FolderType.BodyPart },
            {"clothing", FolderType.Clothing },
            {"current", FolderType.CurrentOutfit },
            {"favorite", FolderType.Favorites },
            {"gesture", FolderType.Gesture },
            {"inbox", FolderType.Inbox },
            {"landmark", FolderType.Landmark },
            {"lsl", FolderType.LSLText },
            {"lstndfnd", FolderType.LostAndFound },
            {"my_otfts", FolderType.MyOutfits },
            {"notecard", FolderType.Notecard },
            {"object", FolderType.Object },
            {"outbox", FolderType.Outbox },
            {"root", FolderType.Root },
            {"snapshot", FolderType.Snapshot },
            {"sound", FolderType.Sound },
            {"texture", FolderType.Texture },
            {"trash", FolderType.Trash }
        };

        #region ISharedRegionModule Members

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            m_inventoryAPIv3Url = config.GetString("Cap_InventoryAPIv3", string.Empty);

            if (m_inventoryAPIv3Url.Length > 0)
                Enabled = true;

            if (m_inventoryAPIv3Url != "localhost")
                return;

            CategoryMethodHandlers.Add("GET", HandleGetCategory);
            CategoryMethodHandlers.Add("POST", HandlePostCategory);
            CategoryMethodHandlers.Add("PATCH", HandlePatchCategory);
            CategoryMethodHandlers.Add("DELETE", HandleDeleteCategory);

            ItemMethodHandlers.Add("GET", HandleGetItem);
            ItemMethodHandlers.Add("PATCH", HandlePatchItem);
            ItemMethodHandlers.Add("DELETE", HandleDeleteItem);
        }

        public void AddRegion(Scene s)
        {
        }

        public void RemoveRegion(Scene s)
        {
            if (!Enabled)
                return;

            s.EventManager.OnRegisterCaps -= RegisterCaps;

            --NumberOfScenes;
            if (NumberOfScenes <= 0)
            {
                m_inventoryService = null;
            }
        }

        public void RegionLoaded(Scene s)
        {
            if (!Enabled)
                return;

            if (m_inventoryService == null)
                m_inventoryService = s.InventoryService;

            if (m_inventoryService != null)
            {
                s.EventManager.OnRegisterCaps += RegisterCaps;
                ++NumberOfScenes;
            }
        }

        public void PostInitialise() { }

        public void Close() { }

        public string Name { get { return "AgentInventoryServiceModule"; } }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            if (m_inventoryAPIv3Url == "localhost")
            {
                caps.RegisterSimpleHandler("InventoryAPIv3",
                    new SimpleStreamHandler($"/{UUID.Random()}", delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
                    {
                        HandleInventoryAPIv3(httpRequest, httpResponse, agentID);
                    }), true, true);
            }
            else
            {
                caps.RegisterHandler("InventoryAPIv3", m_inventoryAPIv3Url);
            }
        }

        private void HandleInventoryAPIv3(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, UUID agentID)
        {
            int index = httpRequest.UriPath.IndexOf('/', 1);
            string tr = httpRequest.UriPath.Substring(index + 1);
            var split = tr.Split('/');

            if (split.Length < 2)
            {
                ThrowError(httpResponse, "Not enough args.");
                return;
            }

            OSDMap request_map = null;

            try
            {
                request_map = (OSDMap)OSDParser.DeserializeLLSDXml(httpRequest.InputStream);
            }
            catch { }

            string type = split[0];
            string id = split[1];

            if (type == "category")
            {
                var request = new CategoryRequestData
                {
                    AgentID = agentID,
                    Method = httpRequest.HttpMethod,
                    Map = request_map
                };

                ExtractParams(httpRequest, out request.TransactionID, out request.Depth, out request.Simulate);

                if (split.Length > 2)
                {
                    request.ContentType = split[2];
                }

                if (UUID.TryParse(id, out UUID folder_id))
                {
                    request.Folder = m_inventoryService.GetFolder(agentID, folder_id);
                }
                else if (NamedFolders.TryGetValue(id, out var folder_type))
                {
                    request.Folder = m_inventoryService.GetFolderForType(agentID, folder_type);
                }
                else
                {
                    ThrowError(httpResponse, $"Unrecognized special folder name: {folder_type}.");
                    return;
                }

                if (request.Folder is null)
                {
                    ThrowError(httpResponse, "Category not found.");
                    return;
                }

                if (CategoryMethodHandlers.TryGetValue(httpRequest.HttpMethod, out var handle))
                {
                    handle(httpResponse, request);
                }
            }
            else if (type == "item")
            {
                var request = new ItemRequestData
                {
                    AgentID = agentID,
                    Method = httpRequest.HttpMethod,
                    Map = request_map
                };

                ExtractParams(httpRequest, out request.TransactionID, out _, out request.Simulate);

                if (UUID.TryParse(id, out UUID item_id))
                {
                    request.Item = m_inventoryService.GetItem(agentID, item_id);
                }

                if(request.Item is null)
                {
                    ThrowError(httpResponse, "Item not found.");
                    return;
                }

                if (ItemMethodHandlers.TryGetValue(httpRequest.HttpMethod, out var handle))
                {
                    handle(httpResponse, request);
                }
            }
        }

        private void ThrowError(IOSHttpResponse httpResponse, string error)
        {
            osUTF8 response = LLSDxmlEncode2.Start(131072, true);
            LLSDxmlEncode2.AddMap(response);
            LLSDxmlEncode2.AddElem("error_description", error, response);
            LLSDxmlEncode2.AddEndMap(response);

            httpResponse.RawBuffer = LLSDxmlEncode2.EndToNBBytes(response);
            httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
            httpResponse.ContentType = "application/xml";
        }

        bool ExtractParams(IOSHttpRequest httpRequest, out UUID transaction_id, out int depth, out bool simulate)
        {
            NameValueCollection query = httpRequest.QueryString;

            string str_tid = query.GetOne("tid");
            string str_depth = query.GetOne("depth");
            string str_simulate = query.GetOne("simulate");

            try
            {
                transaction_id = (string.IsNullOrEmpty(str_tid) ? UUID.Zero : UUID.Parse(str_tid));
                depth = (string.IsNullOrEmpty(str_depth) ? 1 : int.Parse(str_depth));
                simulate = (string.IsNullOrEmpty(str_simulate) ? false : bool.Parse(str_depth));
                return true;
            }
            catch
            {
                transaction_id = UUID.Zero;
                depth = 0;
                simulate = false;
                return false;
            }
        }

        #region POST

        private void HandlePostCategory(IOSHttpResponse httpResponse, CategoryRequestData request)
        {
            var agentID = request.AgentID;
            var folder = request.Folder;
            var req = request.Map;

            CreateAllFolders(req, agentID, out var createdFolders);
            CreateAllLinks(req, agentID, folder.ID, out var createdLinks);


            osUTF8 response = LLSDxmlEncode2.Start(131072, true);

            LLSDxmlEncode2.AddMap(response);

            AddFolderDetails(folder, response);

            LLSDxmlEncode2.AddMap("_embedded", response);

            if (createdFolders.Count > 0)
            {
                LLSDxmlEncode2.AddMap("categories", response);

                foreach (var cat in createdFolders)
                {
                    LLSDxmlEncode2.AddMap(cat.ID.ToString(), response);
                    RecursivelyAddFolder(cat, response, "children", 0, 0);
                    LLSDxmlEncode2.AddEndMap(response);
                }

                LLSDxmlEncode2.AddEndMap(response);
            }

            if (createdLinks.Count > 0)
            {
                LLSDxmlEncode2.AddMap("links", response);

                foreach (var link in createdLinks)
                {
                    LLSDxmlEncode2.AddMap(link.ID.ToString(), response);


                    AddItemDetails(link, response);

                    AddLinkLinks(link, response);

                    LLSDxmlEncode2.AddElem("linked_id", link.AssetID.ToString(), response);

                    var linked_item = m_inventoryService.GetItem(agentID, link.AssetID);

                    LLSDxmlEncode2.AddElem("_broken", (linked_item is null), response);

                    if (linked_item is not null)
                    {
                        LLSDxmlEncode2.AddMap("_embedded", response);
                        LLSDxmlEncode2.AddMap("item", response);

                        AddItemDetails(link, response);
                        AddPermissionsInfo(link, response);
                        AddSaleInfo(link, response);
                        AddItemLinks(link, response);

                        LLSDxmlEncode2.AddEndMap(response);
                        LLSDxmlEncode2.AddEndMap(response);
                    }
                    else
                    {
                        LLSDxmlEncode2.AddEmptyMap("_embedded", response);
                    }

                    LLSDxmlEncode2.AddEndMap(response);
                }

                LLSDxmlEncode2.AddEndMap(response);
            }

            LLSDxmlEncode2.AddEndMap(response);

            AddUriElem("_base_uri", "slcap://InventoryAPIv3", response);
            AddFolderLinks(folder, response, "/children", true, true);

            LLSDxmlEncode2.AddMap("_updated_category_versions", response);

            LLSDxmlEncode2.AddElem(folder.ID.ToString(), folder.Version, response);

            LLSDxmlEncode2.AddEndMap(response);


            if (createdFolders.Count > 0)
            {
                LLSDxmlEncode2.AddArray("_created_categories", response);

                foreach(var created in createdFolders)
                {
                    LLSDxmlEncode2.AddElem(created.ID.ToString(), response);
                }

                LLSDxmlEncode2.AddEndArray(response);
            }
            else
            {
                LLSDxmlEncode2.AddEmptyArray("_created_categories", response);
            }


            if (createdLinks.Count > 0)
            {
                LLSDxmlEncode2.AddArray("_created_items", response);

                foreach (var created in createdLinks)
                {
                    LLSDxmlEncode2.AddElem(created.ID.ToString(), response);
                }

                LLSDxmlEncode2.AddEndArray(response);
            }
            else
            {
                LLSDxmlEncode2.AddEmptyArray("_created_items", response);
            }

            LLSDxmlEncode2.AddEndMap(response);

            httpResponse.RawBuffer = LLSDxmlEncode2.EndToNBBytes(response);
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/xml";
        }

        private void CreateAllLinks(OSDMap req, UUID agentID, UUID folderID, out List<InventoryItemBase> createdLinks)
        {
            createdLinks = new List<InventoryItemBase>();

            if (req.TryGetValue("links", out OSD tmp))
            {
                var items = tmp as OSDArray;
                foreach (var osd in items)
                {
                    var map = osd as OSDMap;

                    string link_desc = string.Empty;
                    int link_inv_type = 0;
                    UUID link_linked_id = UUID.Zero;
                    string link_name = string.Empty;

                    if (map.TryGetValue("desc", out tmp))
                        link_desc = tmp.ToString();

                    if (map.TryGetValue("inv_type", out tmp))
                        link_inv_type = tmp.AsInteger();

                    if (map.TryGetValue("linked_id", out tmp))
                        link_linked_id = tmp.AsUUID();

                    if (map.TryGetValue("name", out tmp))
                        link_name = tmp.ToString();

                    var link = CreateLink(agentID, folderID, link_name, link_desc, link_inv_type, link_linked_id);

                    if (m_inventoryService.AddItem(link))
                    {
                        createdLinks.Add(link);
                    }
                }
            }
        }

        private void CreateAllFolders(OSDMap req, UUID agentID, out List<InventoryFolderBase> createdFolders)
        {
            createdFolders = new List<InventoryFolderBase>();

            if (req.TryGetValue("categories", out OSD tmp))
            {
                var folders = tmp as OSDArray;

                foreach (var osd in folders)
                {
                    var map = osd as OSDMap;

                    var mk_folder = new InventoryFolderBase(UUID.Random());

                    // category_id

                    if (map.TryGetValue("name", out tmp))
                        mk_folder.Name = tmp.ToString();

                    if (map.TryGetValue("parent_id", out tmp))
                        mk_folder.ParentID = tmp.AsUUID();

                    // type_default

                    mk_folder.Type = (int)InventoryType.Unknown;
                    mk_folder.Owner = agentID;
                    mk_folder.Version = 1;

                    if (m_inventoryService.AddFolder(mk_folder))
                    {
                        createdFolders.Add(mk_folder);
                    }
                }
            }
        }

        InventoryItemBase CreateLink(UUID agentID, UUID folderID, string name, string desc, int inv_type, UUID item_id)
        {
            var item = new InventoryItemBase(UUID.Random());
            item.InvType = inv_type;

            item.Owner = agentID;
            item.CreatorId = agentID.ToString();

            item.Name = name;
            item.Description = desc;

            item.AssetType = (int)AssetType.Link;
            item.AssetID = item_id;
            item.Folder = folderID;

            item.CreationDate = (int)Utils.GetUnixTime();

            return item;
        }

        #endregion

        #region PATCH

        private void HandlePatchItem(IOSHttpResponse httpResponse, ItemRequestData request)
        {
            var item = request.Item;
            var req = request.Map;

            OSD tmp;

            if (req.TryGetValue("name", out tmp))
            {
                item.Name = tmp;
            }

            if (req.TryGetValue("desc", out tmp))
            {
                item.Description = tmp;
            }

            if (req.TryGetValue("permissions", out tmp))
            {
                var perms = (OSDMap)tmp;

                if (perms.TryGetValue("next_owner_mask", out tmp))
                    item.NextPermissions = tmp.AsUInteger() & item.BasePermissions;

                if (perms.TryGetValue("group_mask", out tmp))
                    item.GroupPermissions = tmp.AsUInteger();

                if (perms.TryGetValue("everyone_mask", out tmp))
                    item.EveryOnePermissions = tmp.AsUInteger();
            }

            if (req.TryGetValue("sale_info", out tmp))
            {
                var info = (OSDMap)tmp;

                if (info.TryGetValue("sale_price", out tmp))
                    item.SalePrice = tmp.AsInteger();

                if (info.TryGetValue("sale_type", out tmp))
                    item.SaleType = (byte)SaleTypeFromString(tmp.AsString());
            }

            if (req.TryGetValue("thumbnail", out tmp))
            {
                var thumb = (OSDMap)tmp;

                if (thumb.TryGetValue("asset_id", out tmp))
                {
                    UUID thumbnail_id = tmp.AsUUID();
                    // todo
                }
            }

            osUTF8 response = LLSDxmlEncode2.Start(131072, true);
            LLSDxmlEncode2.AddMap(response);

            AddItemDetails(item, response);
            AddPermissionsInfo(item, response);
            AddSaleInfo(item, response);
            AddItemLinks(item, response);

            AddUriElem("_base_uri", "slcap://InventoryAPIv3", response);

            LLSDxmlEncode2.AddEmptyMap("_updated_category_versions", response);

            LLSDxmlEncode2.AddArray("_updated_items", response);
            LLSDxmlEncode2.AddElem(item.ID, response);
            LLSDxmlEncode2.AddEndArray(response);

            LLSDxmlEncode2.AddMap("thumbnail", response);
            LLSDxmlEncode2.AddElem("asset_id", UUID.Zero, response);
            LLSDxmlEncode2.AddEndMap(response);

            LLSDxmlEncode2.AddEndMap(response);

            httpResponse.RawBuffer = LLSDxmlEncode2.EndToNBBytes(response);
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/xml";
        }

        private void HandlePatchCategory(IOSHttpResponse httpResponse, CategoryRequestData request)
        {
            var agentID = request.AgentID;
            var req = request.Map;
            var folder = request.Folder;

            OSD tmp;
            if (req.TryGetValue("name", out tmp))
            {
                folder.Name = tmp;
            }

            osUTF8 response = LLSDxmlEncode2.Start(131072, true);
            LLSDxmlEncode2.AddMap(response);

            AddFolderDetails(folder, response);
            AddUriElem("_base_uri", "slcap://InventoryAPIv3", response);
            AddFolderLinks(folder, response, string.Empty, true);

            LLSDxmlEncode2.AddEmptyMap("_updated_category_versions", response);

            LLSDxmlEncode2.AddArray("_updated_categories", response);
            LLSDxmlEncode2.AddElem(folder.ID, response);
            LLSDxmlEncode2.AddEndArray(response);

            LLSDxmlEncode2.AddEndMap(response);

            httpResponse.RawBuffer = LLSDxmlEncode2.EndToNBBytes(response);
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/xml";
        }

        SaleType SaleTypeFromString(string str)
        {
            switch (str)
            {
                case "orig":
                    return SaleType.Original;
                case "copy":
                    return SaleType.Copy;
                case "cntn":
                    return SaleType.Contents;
                case "not":
                default:
                    return SaleType.Not;
            }
        }

        #endregion

        #region GET

        public void HandleGetCategory(IOSHttpResponse httpResponse, CategoryRequestData request)
        {
            var folder = request.Folder;

            osUTF8 response = LLSDxmlEncode2.Start(131072, true);

            LLSDxmlEncode2.AddMap(response);
            RecursivelyAddFolder(folder, response, request.ContentType, request.Depth, 0);
            LLSDxmlEncode2.AddEndMap(response);

            AddUriElem("_base_uri", "slcap://InventoryAPIv3", response);

            string path = string.Empty;
            if (request.ContentType != "")
                path = $"/{request.ContentType}";

            AddFolderLinks(folder, response, path, true, request.ContentType != string.Empty);

            LLSDxmlEncode2.AddEndMap(response);

            httpResponse.RawBuffer = LLSDxmlEncode2.EndToNBBytes(response);
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/xml";
        }

        private void HandleGetItem(IOSHttpResponse httpResponse, ItemRequestData request)
        {
            var item = request.Item;

            osUTF8 response = LLSDxmlEncode2.Start(131072, true);
            LLSDxmlEncode2.AddMap(response);

            AddItemDetails(item, response);
            AddPermissionsInfo(item, response);
            AddSaleInfo(item, response);
            AddItemLinks(item, response);

            AddUriElem("_base_uri", "slcap://InventoryAPIv3", response);

            LLSDxmlEncode2.AddEndMap(response);

            //httpResponse.AddHeader("ContentLocation", $"slcap://InventoryAPIv3/item/{item.ID}");
            httpResponse.RawBuffer = LLSDxmlEncode2.EndToNBBytes(response);
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/xml";
        }

        #endregion

        #region DELETE

        private void HandleDeleteItem(IOSHttpResponse httpResponse, ItemRequestData request)
        {
            var agentID = request.AgentID;
            var item = request.Item;

            var folder = m_inventoryService.GetFolder(agentID, item.Folder);

            m_inventoryService.DeleteItems(agentID, new List<UUID> { item.ID });

            osUTF8 response = LLSDxmlEncode2.Start(131072, true);
            LLSDxmlEncode2.AddMap(response);


            LLSDxmlEncode2.AddArray("_category_items_removed", response);
            LLSDxmlEncode2.AddElem(item.ID.ToString(), response);
            LLSDxmlEncode2.AddEndArray(response);

            LLSDxmlEncode2.AddMap("_updated_category_versions", response);
            LLSDxmlEncode2.AddElem(folder.ID.ToString(), folder.Version, response);
            LLSDxmlEncode2.AddEndMap(response);

            LLSDxmlEncode2.AddEmptyArray("_active_gestures_removed", response);
            LLSDxmlEncode2.AddEmptyArray("_broken_links_removed", response);
            LLSDxmlEncode2.AddEmptyArray("_attachments_removed", response);
            LLSDxmlEncode2.AddEmptyArray("_wearables_removed", response);


            LLSDxmlEncode2.AddEndMap(response);

            httpResponse.RawBuffer = LLSDxmlEncode2.EndToNBBytes(response);
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/xml";
        }

        private void HandleDeleteCategory(IOSHttpResponse httpResponse, CategoryRequestData request)
        {
            osUTF8 response = LLSDxmlEncode2.Start(131072, true);
            LLSDxmlEncode2.AddMap(response);

            LLSDxmlEncode2.AddEmptyMap("_updated_category_versions", response);

            if (request.ContentType == "")
            {
                if(request.Folder.Type != -1)
                {
                    ThrowError(httpResponse, "Cannot delete type folders.");
                    return;
                }

                m_inventoryService.DeleteFolders(request.AgentID, new List<UUID> { request.Folder.ID});

                LLSDxmlEncode2.AddArray("_categories_removed", response);
                LLSDxmlEncode2.AddElem(request.Folder.ID, response);
                LLSDxmlEncode2.AddEndArray(response);

                LLSDxmlEncode2.AddEmptyArray("_category_items_removed", response);
            }
            else if(request.ContentType == "children")
            {
                // Not sure why PurgeFolder doesn't take an agent ID
                if(request.Folder.Owner != request.AgentID)
                {
                    ThrowError(httpResponse, "You do not own this folder.");
                    return;
                }

                var content = m_inventoryService.GetFolderContent(request.AgentID, request.Folder.ID);

                LLSDxmlEncode2.AddArray("_categories_removed", response);
                foreach(var cat in content.Folders)
                {
                    LLSDxmlEncode2.AddElem(cat.ID, response);
                }
                LLSDxmlEncode2.AddEndArray(response);

                LLSDxmlEncode2.AddArray("_category_items_removed", response);
                foreach (var item in content.Items)
                {
                    LLSDxmlEncode2.AddElem(item.ID, response);
                }
                LLSDxmlEncode2.AddEndArray(response);

                // This right here is a perfect example of why this should be moved to the robust.
                // Purge could just respond with what has been deleted.

                m_inventoryService.PurgeFolder(request.Folder);
            }

            LLSDxmlEncode2.AddEmptyArray("_active_gestures_removed", response);
            LLSDxmlEncode2.AddEmptyArray("_broken_links_removed", response);
            LLSDxmlEncode2.AddEmptyArray("_attachments_removed", response);
            LLSDxmlEncode2.AddEmptyArray("_wearables_removed", response);

            LLSDxmlEncode2.AddEndMap(response);

            httpResponse.RawBuffer = LLSDxmlEncode2.EndToNBBytes(response);
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/xml";
        }

        #endregion

        void RecursivelyAddFolder(InventoryFolderBase folder, osUTF8 response, string type, int depth, int current_depth)
        {
            AddFolderDetails(folder, response);

            if (type == string.Empty)
                return;

            if (current_depth < depth)
            {
                var content = m_inventoryService.GetFolderContent(folder.Owner, folder.ID);

                LLSDxmlEncode2.AddMap("_embedded", response);

                if (type == "categories" || type == "children")
                {
                    if (content.Folders.Count == 0)
                        LLSDxmlEncode2.AddEmptyMap("categories", response);
                    else
                    {
                        LLSDxmlEncode2.AddMap("categories", response);
                        foreach (var sub in content.Folders)
                        {
                            LLSDxmlEncode2.AddMap(folder.ID.ToString(), response);
                            RecursivelyAddFolder(sub, response, type, depth, current_depth + 1);
                            LLSDxmlEncode2.AddEndMap(response);
                        }
                        LLSDxmlEncode2.AddEndMap(response);
                    }
                }

                List<InventoryItemBase> real_items = new List<InventoryItemBase>();
                List<InventoryItemBase> link_items = new List<InventoryItemBase>();

                foreach (var item in content.Items)
                {
                    if (item.AssetType == (int)AssetType.Link)
                        link_items.Add(item);
                    else
                        real_items.Add(item);
                }

                if (type == "items" || type == "children")
                {
                    if (real_items.Count == 0)
                        LLSDxmlEncode2.AddEmptyMap("items", response);
                    else
                    {
                        LLSDxmlEncode2.AddMap("items", response);
                        foreach (var item in real_items)
                        {
                            AddItemMap(item, response);
                        }
                        LLSDxmlEncode2.AddEndMap(response);
                    }
                }

                if (type == "links" || type == "items" || type == "children")
                {
                    if (link_items.Count == 0)
                        LLSDxmlEncode2.AddEmptyMap("links", response);
                    else
                    {
                        LLSDxmlEncode2.AddMap("links", response);
                        foreach (var item in link_items)
                        {
                            AddLinkMap(item, response);
                        }
                        LLSDxmlEncode2.AddEndMap(response);
                    }
                }

                LLSDxmlEncode2.AddEndMap(response);
            }

            AddFolderLinks(folder, response);
        }

        #region Formatting

        private void AddItemMap(InventoryItemBase item, osUTF8 response)
        {
            LLSDxmlEncode2.AddMap(item.ID.ToString(), response);

            AddItemDetails(item, response);
            AddPermissionsInfo(item, response);
            AddSaleInfo(item, response);
            AddItemLinks(item, response);

            LLSDxmlEncode2.AddEndMap(response);
        }

        private void AddLinkMap(InventoryItemBase item, osUTF8 response)
        {
            LLSDxmlEncode2.AddMap(item.ID.ToString(), response);

            AddLinkDetails(item, response);

            AddLinkLinks(item, response);

            LLSDxmlEncode2.AddElem("linked_id", item.AssetID, response);

            var linked_item = m_inventoryService.GetItem(item.Owner, item.AssetID);

            if (linked_item is not null)
            {
                AddBoolElem("_broken", false, response);

                LLSDxmlEncode2.AddMap("_embedded", response);

                LLSDxmlEncode2.AddMap("item", response);

                AddItemDetails(linked_item, response);
                AddPermissionsInfo(linked_item, response);
                AddSaleInfo(linked_item, response);
                AddItemLinks(linked_item, response);

                LLSDxmlEncode2.AddEndMap(response);

                LLSDxmlEncode2.AddEndMap(response);
            }
            else
            {
                AddBoolElem("_broken", true, response);
            }

            LLSDxmlEncode2.AddEndMap(response);
        }

        private void AddFolderLinks(InventoryFolderBase folder, osUTF8 response, string path = "", bool types = false, bool include_category = true)
        {
            LLSDxmlEncode2.AddMap("_links", response);
            LLSDxmlEncode2.AddMap("self", response);
            AddUriElem("href", $"/category/{folder.ID}{path}", response);
            LLSDxmlEncode2.AddEndMap(response);
            LLSDxmlEncode2.AddMap("parent", response);
            AddUriElem("href", $"/category/{folder.ParentID}", response);
            LLSDxmlEncode2.AddEndMap(response);

            if (types)
            {
                LLSDxmlEncode2.AddMap("children", response);
                AddUriElem("href", $"/category/{folder.ID}/children", response);
                LLSDxmlEncode2.AddEndMap(response);

                LLSDxmlEncode2.AddMap("categories", response);
                AddUriElem("href", $"/category/{folder.ID}/categories", response);
                LLSDxmlEncode2.AddEndMap(response);

                LLSDxmlEncode2.AddMap("items", response);
                AddUriElem("href", $"/category/{folder.ID}/items", response);
                LLSDxmlEncode2.AddEndMap(response);

                LLSDxmlEncode2.AddMap("links", response);
                AddUriElem("href", $"/category/{folder.ID}/links", response);
                LLSDxmlEncode2.AddEndMap(response);
            }

            if (include_category)
            {
                LLSDxmlEncode2.AddMap("category", response);
                AddUriElem("href", $"/category/{folder.ID}", response);
                LLSDxmlEncode2.AddElem("name", "self", response);
                LLSDxmlEncode2.AddEndMap(response);
            }

            LLSDxmlEncode2.AddEndMap(response);
        }

        private void AddItemLinks(InventoryItemBase item, osUTF8 response)
        {
            LLSDxmlEncode2.AddMap("_links", response);
            LLSDxmlEncode2.AddMap("item", response);
            AddUriElem("href", $"/item/{item.ID}", response);
            LLSDxmlEncode2.AddEndMap(response);
            LLSDxmlEncode2.AddMap("parent", response);
            AddUriElem("href", $"/category/{item.Folder}", response);
            LLSDxmlEncode2.AddEndMap(response);


            LLSDxmlEncode2.AddEndMap(response);
        }

        private void AddLinkLinks(InventoryItemBase item, osUTF8 response)
        {
            LLSDxmlEncode2.AddMap("_links", response);

            LLSDxmlEncode2.AddMap("self", response);
            AddUriElem("href", $"/item/{item.ID}", response);
            LLSDxmlEncode2.AddEndMap(response);

            LLSDxmlEncode2.AddMap("parent", response);
            AddUriElem("href", $"/category/{item.Folder}", response);
            LLSDxmlEncode2.AddEndMap(response);

            LLSDxmlEncode2.AddMap("item", response);
            AddUriElem("href", $"/item/{item.AssetID}", response);
            LLSDxmlEncode2.AddElem("name", "link", response);
            LLSDxmlEncode2.AddEndMap(response);

            LLSDxmlEncode2.AddEndMap(response);
        }

        private void AddFolderDetails(InventoryFolderBase sub, osUTF8 response)
        {
            LLSDxmlEncode2.AddElem("agent_id", sub.Owner, response);
            LLSDxmlEncode2.AddElem("category_id", sub.ID, response);
            LLSDxmlEncode2.AddElem("parent_id", sub.ParentID, response);
            LLSDxmlEncode2.AddElem("name", sub.Name, response);
            LLSDxmlEncode2.AddElem("type_default", sub.Type, response);
            LLSDxmlEncode2.AddElem("version", sub.Version, response);
        }

        private void AddItemDetails(InventoryItemBase item, osUTF8 response)
        {
            LLSDxmlEncode2.AddElem("agent_id", item.Owner, response);
            LLSDxmlEncode2.AddElem("item_id", item.ID, response);
            LLSDxmlEncode2.AddElem("parent_id", item.Folder, response);
            LLSDxmlEncode2.AddElem("asset_id", item.AssetID, response);
            LLSDxmlEncode2.AddElem("name", item.Name, response);
            LLSDxmlEncode2.AddElem("desc", item.Description, response);
            LLSDxmlEncode2.AddElem("type", item.AssetType, response);
            LLSDxmlEncode2.AddElem("inv_type", item.InvType, response);
            LLSDxmlEncode2.AddElem("flags", (int)item.Flags, response);
            LLSDxmlEncode2.AddElem("created_at", item.CreationDate, response);
        }

        private void AddLinkDetails(InventoryItemBase item, osUTF8 response)
        {
            LLSDxmlEncode2.AddElem("agent_id", item.Owner, response);
            LLSDxmlEncode2.AddElem("item_id", item.ID, response);
            LLSDxmlEncode2.AddElem("parent_id", item.Folder, response);
            LLSDxmlEncode2.AddElem("name", item.Name, response);
            LLSDxmlEncode2.AddElem("desc", item.Description, response);
            LLSDxmlEncode2.AddElem("type", item.AssetType, response);
            LLSDxmlEncode2.AddElem("inv_type", item.InvType, response);
            LLSDxmlEncode2.AddElem("created_at", item.CreationDate, response);
        }

        private void AddPermissionsInfo(InventoryItemBase item, osUTF8 response)
        {
            LLSDxmlEncode2.AddMap("permissions", response);
            LLSDxmlEncode2.AddElem("creator_id", item.CreatorIdAsUuid, response);
            LLSDxmlEncode2.AddElem("owner_id", item.Owner, response);
            LLSDxmlEncode2.AddElem("group_id", item.GroupID, response);
            LLSDxmlEncode2.AddElem("base_mask", (int)item.BasePermissions, response);
            LLSDxmlEncode2.AddElem("owner_mask", (int)item.CurrentPermissions, response);
            LLSDxmlEncode2.AddElem("everyone_mask", (int)item.EveryOnePermissions, response);
            LLSDxmlEncode2.AddElem("group_mask", (int)item.GroupPermissions, response);
            LLSDxmlEncode2.AddElem("next_owner_mask", (int)item.NextPermissions, response);
            LLSDxmlEncode2.AddElem("last_owner_id", UUID.Zero, response); // todo
            LLSDxmlEncode2.AddEndMap(response);
        }

        private void AddSaleInfo(InventoryItemBase item, osUTF8 response)
        {
            LLSDxmlEncode2.AddMap("sale_info", response);
            LLSDxmlEncode2.AddElem("sale_type", item.SaleType, response);
            LLSDxmlEncode2.AddElem("sale_price", item.SalePrice, response);
            LLSDxmlEncode2.AddEndMap(response);
        }

        public static void AddBoolElem(string name, bool b, osUTF8 sb)
        {
            sb.Append(osUTF8Const.XMLkeyStart);
            sb.AppendASCII(name);
            sb.Append(osUTF8Const.XMLkeyEnd);
            sb.Append($"<boolean>{(b ? "true" : "false")}</boolean>");
        }

        public static void AddUriElem(string name, string uri, osUTF8 sb)
        {
            sb.Append(osUTF8Const.XMLkeyStart);
            sb.AppendASCII(name);
            sb.Append(osUTF8Const.XMLkeyEnd);

            if (string.IsNullOrEmpty(uri))
            {
                sb.Append(osUTF8Const.XMLuriEmpty);
                return;
            }

            sb.Append(osUTF8Const.XMLuriStart);
            sb.Append(uri);
            sb.Append(osUTF8Const.XMLuriEnd);
        }

        #endregion
    }
}
