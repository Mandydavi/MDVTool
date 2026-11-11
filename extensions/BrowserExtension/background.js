'use strict';

const APP_BASE = 'http://127.0.0.1:18765';
const MENU_ID = 'mdv-download';

// tabId -> 该标签页最近捕获的媒体地址（最新的在末尾）
const capturedByTab = new Map();

function latestMedia(tabId) {
  const list = capturedByTab.get(tabId);
  return list && list.length ? list[list.length - 1] : null;
}

chrome.webRequest.onResponseStarted.addListener(
  function (details) {
    const tabId = details.tabId;
    if (tabId === undefined || tabId < 0 || !details.url) return;

    let list = capturedByTab.get(tabId);
    if (!list) {
      list = [];
      capturedByTab.set(tabId, list);
    }
    const at = list.indexOf(details.url);
    if (at !== -1) list.splice(at, 1);
    list.push(details.url);
    if (list.length > 20) list.shift();
  },
  {
    urls: ['*://*/*.m3u8*', '*://*/*.mpd*', '*://*/*.mp4*'],
    types: ['media', 'xmlhttprequest', 'other']
  }
);

function ensureMenu() {
  chrome.contextMenus.removeAll(function () {
    chrome.contextMenus.create({
      id: MENU_ID,
      title: '用 MDV Tool 下载',
      contexts: ['link', 'video', 'audio', 'page']
    });
  });
}

chrome.runtime.onInstalled.addListener(ensureMenu);

chrome.contextMenus.onClicked.addListener(function (info, tab) {
  if (info.menuItemId !== MENU_ID) return;

  const tabId = tab && tab.id;
  const recent = tabId !== undefined ? latestMedia(tabId) : null;
  const url = info.linkUrl || info.srcUrl || recent || info.pageUrl || (tab && tab.url) || '';
  if (!url) {
    console.warn('MDV Tool: 没有找到可下载的链接');
    return;
  }
  postToApp(url, (tab && tab.title) || '', info.pageUrl || (tab && tab.url) || '');
});

chrome.runtime.onMessage.addListener(function (msg, sender) {
  if (!msg || msg.type !== 'download') return false;
  const tab = sender && sender.tab ? sender.tab : {};
  postToApp(msg.url || tab.url || '', msg.title || tab.title || '', msg.pageUrl || tab.url || '');
  return false;
});

// 发送前先探测主程序是否在线；离线时只写控制台，不弹 alert
async function postToApp(url, title, pageUrl) {
  if (!url) return;

  try {
    const ping = await fetch(APP_BASE + '/ping', { method: 'GET' });
    if (!ping.ok) throw new Error('ping status ' + ping.status);
  } catch (e) {
    console.warn('MDV Tool: 主程序未启动或未监听 127.0.0.1:18765，已忽略本次下载请求');
    return;
  }

  try {
    const resp = await fetch(APP_BASE + '/add', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        url: url,
        title: title || '',
        pageUrl: pageUrl || ''
      })
    });
    if (!resp.ok) console.warn('MDV Tool: /add 返回状态 ' + resp.status);
  } catch (e) {
    console.warn('MDV Tool: 发送下载请求失败：' + e);
  }
}
