﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.WpfPropertyGrid.Attributes;
using System.Windows.Input;
using Fiddler;
using Hawk.Core.Connectors;
using Hawk.Core.Utils;
using Hawk.Core.Utils.Logs;
using Hawk.Core.Utils.MVVM;
using Hawk.Core.Utils.Plugins;
using Hawk.ETL.Crawlers;
using Hawk.ETL.Interfaces;
using Hawk.ETL.Managements;
using HtmlAgilityPack;

namespace Hawk.ETL.Process
{
    [XFrmWork("网页采集器", "自动采集网络脚本", "/XFrmWork.DataMining.Process;component/Images/hadoop.jpg",
        "数据采集和处理")]
    public class SmartCrawler : AbstractProcessMethod, IView
    {
        /// <summary>
        ///     当URL发生变化时，自动访问之
        /// </summary>
        public static bool AutoVisitUrl = true;

        private readonly Regex extract = new Regex(@"\[(\w+)\]");
        private readonly HttpHelper helper;
        private bool _isSuperMode;
        private string _rootXPath;
        private XPathAnalyzer.CrawTarget CrawTarget;
        private IEnumerator<string> currentXPaths;
        public bool enableRefresh = true;
        public HtmlDocument HtmlDoc = new HtmlDocument();
        private bool isBusy;

        /// <summary>
        ///     上一次采集到的数据条目数
        /// </summary>
        public int lastCrawCount;

        private string selectName = "属性0";
        private string selectText = "";
        private string selectXPath = "";
        private string url = "";
        private string urlHTML = "";
        private int xpath_count;

        public SmartCrawler()
        {
            Http = new HttpItem();
            CrawlItems = new ObservableCollection<CrawlItem>();
            helper = new HttpHelper();
            // SelectText = "自带投影自带音箱不发音";
            URL = "";
            SelectText = "";
            IsMultiData = ListType.List;
            IsAttribute = true;
            URL = "www.cnblogs.com";
        }

        [Browsable(false)]
        public string URLHTML
        {
            get { return urlHTML; }
            set
            {
                if (urlHTML != value)
                {
                    urlHTML = value;
                    OnPropertyChanged("URLHTML");
                }
            }
        }

        [Browsable(false)]
        public string URL
        {
            get { return url; }
            set
            {
                if (url == value) return;
                url = value;
                OnPropertyChanged("URL");
                if (AutoVisitUrl)
                    VisitUrlAsync();
            }
        }

        [Browsable(false)]
        public ReadOnlyCollection<ICommand> Commands3
        {
            get
            {
                return CommandBuilder.GetCommands(
                    this,
                    new[]
                    {
                        new Command("刷新网页", obj => VisitUrlAsync()),
                        new Command("复制到剪切板", obj => CopytoClipBoard())
                    });
            }
        }

        [LocalizedDisplayName("执行")]
        [LocalizedCategory("2.属性提取")]
        [PropertyOrder(6)]
        public ReadOnlyCollection<ICommand> Commands2
        {
            get
            {
                return CommandBuilder.GetCommands(
                    this,
                    new[]
                    {
                        new Command("添加字段", obj => AddNewItem(),
                            obj =>
                                string.IsNullOrEmpty(SelectName) == false && string.IsNullOrEmpty(SelectXPath) == false),
                        new Command("搜索XPath", obj => GetXPathAsync(),
                            obj =>
                                currentXPaths != null),
                        new Command("手气不错",
                            obj => FeelLucky(),
                            obj => IsMultiData == ListType.List && isBusy == false
                            ),
                        new Command("提取测试", obj =>
                        {
                            if (!(CrawlItems.Count > 0).SafeCheck("属性数量不能为空"))
                                return;

                            if (IsMultiData == ListType.List && CrawlItems.Count < 2)
                            {
                                MessageBox.Show("列表模式下，属性数量不能少于2个", "提示信息");
                                return;
                            }
                            var datas = HtmlDoc.GetDataFromXPath(CrawlItems, IsMultiData, RootXPath);
                            var view = PluginProvider.GetObjectInstance<IDataViewer>("可编辑列表");

                            var r = view.SetCurrentView(datas);
                            ControlExtended.DockableManager.AddDockAbleContent(
                                FrmState.Custom, r, "提取数据测试结果");

                            var rootPath =
                                XPath.GetMaxCompareXPath(CrawlItems.Select(d => d.XPath));
                            if (datas.Count > 1 && string.IsNullOrEmpty(RootXPath) && rootPath.Length > 0 &&
                                IsMultiData == ListType.List &&
                                MessageBox.Show($"检测到列表的根节点为:{rootPath}，是否设置根节点路径？ 此操作有建议有经验用户使用，小白用户请点【否】", "提示信息",
                                    MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                            {
                                RootXPath = rootPath;
                                HtmlDoc.CompileCrawItems(CrawlItems);
                                OnPropertyChanged("RootXPath");
                            }
                        })
                    });
            }
        }

        [LocalizedCategory("2.属性提取")]
        [LocalizedDisplayName("搜索字符")]
        [PropertyOrder(2)]
        public string SelectText
        {
            get { return selectText; }
            set
            {
                if (selectText == value) return;
                selectText = value;
                xpath_count = 0;
                if (string.IsNullOrWhiteSpace(selectText) == false)
                {
                    currentXPaths = HtmlDoc.SearchXPath(SelectText, () => IsAttribute).GetEnumerator();
                    GetXPathAsync();
                }
                OnPropertyChanged("SelectText");
            }
        }

        [LocalizedCategory("2.属性提取")]
        [PropertyOrder(4)]
        [LocalizedDisplayName("获取的XPath")]
        public string SelectXPath
        {
            get { return selectXPath; }
            set
            {
                if (selectXPath != value)
                {
                    selectXPath = value;
                    OnPropertyChanged("SelectXPath");
                }
            }
        }

        [LocalizedCategory("2.属性提取")]
        [PropertyOrder(5)]
        [LocalizedDisplayName("属性名称")]
        [LocalizedDescription("为当前属性命名")]
        public string SelectName
        {
            get { return selectName; }
            set
            {
                if (selectName == value) return;
                selectName = value;
                OnPropertyChanged("SelectName");
            }
        }

        [LocalizedCategory("2.属性提取")]
        [LocalizedDisplayName("读取模式")]
        [LocalizedDescription("当需要获取列表时，选择List,否则选择One")]
        [PropertyOrder(1)]
        public ListType IsMultiData { get; set; }

        [PropertyOrder(6)]
        [LocalizedCategory("2.属性提取")]
        [LocalizedDisplayName("已有属性")]
        public ObservableCollection<CrawlItem> CrawlItems { get; set; }

        [LocalizedCategory("3.动态请求嗅探")]
        [LocalizedDisplayName("请求详情")]
        [PropertyOrder(1)]
        [LocalizedDescription("设置Cookie和其他访问选项")]
        [TypeConverter(typeof (ExpandableObjectConverter))]
        public HttpItem Http { get; set; }

        [Browsable(false)]
        [LocalizedDisplayName("提取标签")]
        [PropertyOrder(4)]
        [LocalizedCategory("2.属性提取")]
        public bool IsAttribute { get; set; }

        [LocalizedCategory("2.属性提取")]
        [LocalizedDescription("当设置该值后，所有属性Path都应该为父节点的子path，而不能是完整的xpath路径")]
        [LocalizedDisplayName("父节点Path")]
        [PropertyOrder(8)]
        public string RootXPath
        {
            get { return _rootXPath; }
            set
            {
                if (_rootXPath != value)
                {
                    _rootXPath = value;
                    OnPropertyChanged("RootXPath");
                }
            }
        }

        [Browsable(false)]
        public bool IsRunning => FiddlerApplication.IsStarted();

        [LocalizedCategory("自动嗅探")]
        [LocalizedDisplayName("保存请求")]
        [Browsable(false)]
        [PropertyOrder(18)]
        public bool CanSave { get; set; }

        [LocalizedCategory("3.动态请求嗅探")]
        [LocalizedDisplayName("执行")]
        [PropertyOrder(20)]
        public ReadOnlyCollection<ICommand> Commands
        {
            get
            {
                return CommandBuilder.GetCommands(
                    this,
                    new[]
                    {
                        new Command("开始", obj => StartVisit(), obj => IsRunning == false),
                        new Command("停止", obj => StopVisit(), obj => IsRunning)
                        //     new Command("模拟登录", obj => { AutoVisit(); })
                    });
            }
        }

        [LocalizedCategory("3.动态请求嗅探")]
        [LocalizedDisplayName("超级模式")]
        [LocalizedDescription("该模式能够强力解析网页内容，但是消耗资源，且会修改原始Html内容")]
        [PropertyOrder(9)]
        public bool IsSuperMode
        {
            get { return _isSuperMode; }
            set
            {
                if (_isSuperMode != value)
                {
                    _isSuperMode = value;
                    if (AutoVisitUrl)
                    {
                        VisitUrlAsync();
                    }

                    OnPropertyChanged("IsSuperMode");
                }
            }
        }

        [LocalizedCategory("3.动态请求嗅探")]
        [LocalizedDisplayName("共享cookie")]
        [LocalizedDescription("填写拥有正确cookie的采集器名称，为空时不起作用，该功能还会获取代理IP等属性，避免重复设置网页采集器")]
        public string ShareCookie { get; set; }

        [Browsable(false)]
        public object UserControl => null;

        [Browsable(false)]
        public FrmState FrmState => FrmState.Large;

        private void CopytoClipBoard()
        {
            Clipboard.SetDataObject(URLHTML);
            XLogSys.Print.Info("已经成功复制到剪贴板");
        }

        private async void GetXPathAsync()
        {
            if (currentXPaths == null)
                return;
            xpath_count++;
            try
            {
                var r = await MainFrm.RunBusyWork(() => currentXPaths.MoveNext(), "正在查询XPath");
                if (r)
                {
                    SelectXPath = currentXPaths.Current;
                    if (string.IsNullOrWhiteSpace(this.SelectName))
                    {
                        this.SelectName = $"属性{this.CrawlItems.Count}";
                    }
                }
                else
                {
                    SelectXPath = "";
                    if (xpath_count > 1)
                    {
                        XLogSys.Print.Warn("找不到其他符合条件的节点，搜索器已经返回开头");
                        currentXPaths = HtmlDoc.SearchXPath(SelectText, () => IsAttribute).GetEnumerator();
                    }
                    else
                    {
                        XLogSys.Print.Info($"在该网页中找不到关键字 {SelectText},可能是动态请求，可以启用【自动嗅探】,并将浏览器页面翻到包含该关键字的位置");
                    }
                }
            }
            catch (Exception ex)
            {
                XLogSys.Print.Warn($"查询XPath时在内部发生异常:" + ex.Message);
            }
        }

        public void FeelLucky()
        {
            isBusy = true;
            var crawTargets = new List<XPathAnalyzer.CrawTarget>();
            var task = TemporaryTask.AddTempTask("网页结构计算中",
                HtmlDoc.SearchPropertiesSmart(CrawlItems, RootXPath, IsAttribute), crawTarget =>
                {
                    crawTargets.Add(crawTarget);
                    var datas = HtmlDoc.GetDataFromXPath(crawTarget.CrawItems, IsMultiData, crawTarget.RootXPath);
                    crawTarget.Datas = datas;
                }, d =>
                {
                    isBusy = false;
                    if (crawTargets.Count == 0)
                    {
                        CrawTarget = null;
                        XLogSys.Print.Warn("没有检查到任何可选的列表页面");
                        return;
                    }

                    var luckModel = new FeelLuckyModel(crawTargets,HtmlDoc);
                    var view = PluginProvider.GetObjectInstance<ICustomView>("手气不错面板") as UserControl;
                    view.DataContext = luckModel;

                    var name = "手气不错";
                    var window = new Window {Title = name};
                    window.WindowState = WindowState.Maximized;
                    window.Content = view;
                    luckModel.SetView(view, window);
                    window.Activate();
                    window.ShowDialog();
                    if (window.DialogResult == true)

                    {
                        var crawTarget = luckModel.CurrentTarget;
                        RootXPath = crawTarget.RootXPath;
                        CrawlItems.Clear();
                        CrawlItems.AddRange(crawTarget.CrawItems.Where(r => r.IsEnabled));
                    }
                });

            SysProcessManager.CurrentProcessTasks.Add(task);
        }

        public override bool Close()
        {
            if (IsRunning)
            {
                StopVisit();
            }
            return base.Close();
        }

        public override FreeDocument DictSerialize(Scenario scenario = Scenario.Database)
        {
            var dict = base.DictSerialize(scenario);
            dict.Add("URL", URL);
            dict.Add("RootXPath", RootXPath);
            dict.Add("IsMultiData", IsMultiData);
            dict.Add("IsSuperMode", IsSuperMode);
            dict.Add("ShareCookie", ShareCookie);
            dict.Add("HttpSet", Http.DictSerialize());
            dict.Children = new List<FreeDocument>();
            dict.Children.AddRange(CrawlItems.Select(d => d.DictSerialize(scenario)));
            return dict;
        }

        public void StopVisit()
        {
            FiddlerApplication.AfterSessionComplete -= FiddlerApplicationAfterSessionComplete;
            FiddlerApplication.Shutdown();
            OnPropertyChanged("IsRunning");
        }

        public void StartVisit()
        {
            if (IsRunning)
                return;
            if (string.IsNullOrWhiteSpace(SelectText))
            {
                MessageBox.Show("请填写包含在页面中的关键字信息：【2.属性提取】->【搜索字符】");
                return;
            }
            ControlExtended.SafeInvoke(() =>
            {
                var url = URL;
                if (url.StartsWith("http") == false)
                    url = "http://" + url;


                CONFIG.bMITM_HTTPS = true;
                FiddlerApplication.AfterSessionComplete += FiddlerApplicationAfterSessionComplete;

                FiddlerApplication.Startup(8888, true, true);
                System.Diagnostics.Process.Start(url);
                OnPropertyChanged("IsRunning");
            }, LogType.Important, "启动嗅探服务");
        }

        private void FiddlerApplicationAfterSessionComplete(Session oSession)
        {
            if (oSession.oRequest.headers == null)
                return;
            var httpitem = new HttpItem {Parameters = oSession.oRequest.headers.ToString()};


            if ((oSession.BitFlags & SessionFlags.IsHTTPS) != 0)
            {
                httpitem.URL = "https://" + oSession.url;
            }
            else
            {
                httpitem.URL = "http://" + oSession.url;
            }
            if (oSession.RequestMethod.ToLower() == "post")
            {
                httpitem.Method = MethodType.POST;
            }

            httpitem.Postdata = Encoding.Default.GetString(oSession.RequestBody);


            if (string.IsNullOrWhiteSpace(SelectText) == false)
            {
                var content = oSession.GetResponseBodyAsString();

                content = JavaScriptAnalyzer.Decode(content);
                if (content.Contains(SelectText) == false)
                {
                    return;
                }
            }
            StopVisit();
            httpitem.DictCopyTo(Http);
            var post = "";
            if (Http.Method == MethodType.POST)
            {
                post = "post请求的内容为:\n" + httpitem.Postdata + "\n";
            }
            var window = MainFrm as Window;
            ControlExtended.UIInvoke(() => { if (window != null) window.Topmost = true; });
            var info = $"已经成功获取嗅探字段！ 真实请求地址:\n{oSession.url}，\n已自动配置了网页采集器，请求类型为{Http.Method}\n {post}已经刷新了网页采集器的内容";
            XLogSys.Print.Info(info);

            ControlExtended.UIInvoke(() => { if (window != null) window.Topmost = false; });
            SniffSucceed?.Invoke(this, new EventArgs());
            URL = oSession.url;
        }

        public event EventHandler<EventArgs> SniffSucceed;
        private bool hasInit = false;
        public override bool Init()
        {
            var r= base.Init();
            hasInit = true;
            return r;
        }

        public override void DictDeserialize(IDictionary<string, object> dicts, Scenario scenario = Scenario.Database)
        {
            base.DictDeserialize(dicts, scenario);
            URL = dicts.Set("URL", URL);
            RootXPath = dicts.Set("RootXPath", RootXPath);
            ShareCookie = dicts.Set("ShareCookie", ShareCookie);
            IsMultiData = dicts.Set("IsMultiData", IsMultiData);
            IsSuperMode = dicts.Set("IsSuperMode", IsSuperMode);
            if (dicts.ContainsKey("HttpSet"))
            {
                var doc2 = dicts["HttpSet"];
                var p = doc2 as IDictionary<string, object>;
                Http.UnsafeDictDeserialize(p);
            }


            if (dicts.ContainsKey("Generator"))
            {
                var doc2 = dicts["Generator"];
                var p = doc2 as IDictionary<string, object>;
            }
            var doc = dicts as FreeDocument;
            if (doc?.Children != null)
            {
                foreach (var child in doc.Children)
                {
                    var item = new CrawlItem();
                    item.DictDeserialize(child);
                    CrawlItems.Add(item);
                }
            }
        }

        private void AddNewItem(bool isAlert = true)
        {
            var path = SelectXPath;
            if (!string.IsNullOrEmpty(RootXPath))
            {
                //TODO: 当XPath路径错误时，需要捕获异常
                HtmlNode root = null;
                try
                {
                    root = HtmlDoc.DocumentNode.SelectSingleNode(RootXPath);
                }
                catch (Exception ex)
                {
                    XLogSys.Print.Error($"{RootXPath}  不能被识别为正确的XPath表达式，请检查");
                }
                if (!(root != null).SafeCheck("使用当前父节点XPath，在文档中找不到任何父节点"))
                    return;
                root = HtmlDoc.DocumentNode.SelectSingleNode(RootXPath)?.ParentNode;

                HtmlNode node = null;
                if (
                    !ControlExtended.SafeInvoke(() => HtmlDoc.DocumentNode.SelectSingleNode(path), ref node,
                        LogType.Info, "检查子节点XPath正确性", true))

                {
                    return;
                }
                if (!(node != null).SafeCheck("使用当前子节点XPath，在文档中找不到任何子节点"))
                    return;

                if (!node.IsAncestor(root) && isAlert)
                {
                    if (
                        MessageBox.Show("当前XPath所在节点不是父节点的后代，请检查对应的XPath，是否依然要添加?", "提示信息", MessageBoxButton.YesNo) ==
                        MessageBoxResult.No)
                    {
                        return;
                    }
                }
                path = XPath.TakeOffPlus(node.XPath, root.XPath);
            }
            if (CrawlItems.FirstOrDefault(d => d.Name == SelectName) == null ||
                MessageBox.Show("已经存在同名的属性，是否依然添加?", "提示信息", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            {
                var item = new CrawlItem {XPath = path, Name = SelectName, SampleData1 = SelectText};

                CrawlItems.Add(item);

                SelectXPath = "";
                SelectName = "";
                XLogSys.Print.Info("成功添加属性");
            }
        }

        public List<FreeDocument> CrawlData(HtmlDocument doc)
        {
            if (CrawlItems.Count == 0)
            {
                var freedoc = new FreeDocument {{"Content", doc.DocumentNode.OuterHtml}};

                return new List<FreeDocument> {freedoc};
            }
            return doc.GetDataFromXPath(CrawlItems, IsMultiData, RootXPath);
        }

        public string GetHtml(string url, out HttpStatusCode code,
            string post = null)
        {
            var mc = extract.Matches(url);
            if (SysProcessManager == null)
            {
                code = HttpStatusCode.NoContent;
                return "";
            }
            var list =
                SysProcessManager.CurrentProcessCollections.ToArray();
            var crawler =
               list.FirstOrDefault(d => d.Name == ShareCookie) as
                    SmartCrawler;
            if (crawler != null)
            {
                Http.ProxyIP = crawler.Http.ProxyIP;
                Http.ProxyPassword = crawler.Http.ProxyPassword;
                Http.ProxyUserName = crawler.Http.ProxyUserName;
                Http.ProxyPort = crawler.Http.ProxyPort;
                if (Http.Parameters != crawler.Http.Parameters)
                {
                    var cookie = crawler.Http.GetHeaderParameter().Get<string>("Cookie");
                    if (string.IsNullOrWhiteSpace(cookie) == false)
                    {
                        Http.SetValue("Cookie", cookie);
                    }
                }
            }
            Dictionary<string, string> paradict = null;
            foreach (Match m in mc)
            {
                if (paradict == null)
                    paradict = XPathAnalyzer.ParseUrl(URL);
                if (paradict == null)
                    break;
                var str = m.Groups[1].Value;
                if (paradict.ContainsKey(str))
                {
                    url = url.Replace(m.Groups[0].Value, paradict[str]);
                }
            }
            WebHeaderCollection headerCollection;
            var content = helper.GetHtml(Http, out headerCollection, out code, url, post);
            content = JavaScriptAnalyzer.Decode(content);
            if (IsSuperMode)
            {
                content = JavaScriptAnalyzer.Parse2XML(content);
            }

            return content;
        }

        public List<FreeDocument> CrawlData(string url, out HtmlDocument doc, out HttpStatusCode code,
            string post = null)
        {
            var content = GetHtml(url, out code, post);
            var datas = new List<FreeDocument>();
            try
            {
                datas = CrawlHtmlData(content, out doc);
                if (datas.Count == 0)
                {
                    XLogSys.Print.InfoFormat("HTML抽取数据失败，url:{0}", url);
                }
            }
            catch (Exception ex)
            {
                doc = new HtmlDocument();
                XLogSys.Print.ErrorFormat("HTML抽取数据失败，url:{0}, 异常为{1}", url, ex.Message);
            }


            return datas;
        }

        public List<FreeDocument> CrawlHtmlData(string html, out HtmlDocument doc
            )
        {
            doc = new HtmlDocument();

            doc.LoadHtml(html);


            return CrawlData(doc);
        }

        private async void VisitUrlAsync()
        {
            if (!enableRefresh)
                return;
            if (hasInit==false)
                return;

            URLHTML = await MainFrm.RunBusyWork(() =>
            {
                HttpStatusCode code;
                return GetHtml(URL, out code);
            });
            if (URLHTML.Contains("尝试自动重定向") &&
                MessageBox.Show("网站提示: " + URLHTML + "\n 通常原因是网站对请求合法性做了检查, 建议填写关键字对网页内容进行自动嗅探", "提示信息",
                    MessageBoxButton.OK) == MessageBoxResult.OK)

            {
                return;
            }


            ControlExtended.SafeInvoke(() =>
            {
                HtmlDoc.LoadHtml(URLHTML);
                if (MainDescription.IsUIForm)
                {
                    var dock = MainFrm as IDockableManager ?? ControlExtended.DockableManager;
                    var control = dock?.ViewDictionary.FirstOrDefault(d => d.Model == this);
                    if (control != null)
                    {
                        dynamic invoke = control.View;
                        if(!IsSuperMode)
                            invoke.UpdateHtml(URLHTML);
                        else
                        {
                            invoke.UpdateHtml("超级模式下内置浏览器不展示内容，请查看左侧的文本内容");
                        }
                    }
                }
            },
                name: "解析html文档");


            if (string.IsNullOrWhiteSpace(selectText) == false)
            {
                currentXPaths = HtmlDoc.SearchXPath(SelectText, () => IsAttribute).GetEnumerator();
                GetXPathAsync();
            }
            OnPropertyChanged("URLHTML");
        }
    }
}