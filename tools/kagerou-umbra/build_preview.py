"""kagerou-umbra.css 를 브라우저에서 확인하는 미리보기 페이지를 만든다.

사용법:  python build_preview.py
         → preview/index.html 을 브라우저로 연다

- kagerou 원본이 이 폴더의 kagerou/ 에 없으면 GitHub 에서 받아 온다
- kagerou 의 스크립트·기본 CSS 를 한 파일에 넣고 XHR 을 가짜로 바꿔 file:// 로 열리게 한다
- 사용자 CSS 는 <link> 로 ../kagerou-umbra.css 를 읽는다. CSS 를 고치고 새로고침하면 반영된다
  (kagerou 처럼 기본 CSS 보다 먼저 붙도록 <head> 맨 앞에 둔다)
- 전투 데이터는 지어낸 값이다 (8인·24인)
"""
import json
import os
import re
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, 'kagerou')
OUT = os.path.join(HERE, 'preview')
REPO = 'https://github.com/hibiyasleep/kagerou.git'

if not os.path.isdir(os.path.join(ROOT, 'overlay')):
    subprocess.run(['git', 'clone', '--depth', '1', REPO, ROOT], check=True)

ov = os.path.join(ROOT, 'overlay')


def rd(p):
    with open(p, encoding='utf-8') as f:
        return f.read()


files = {
    'css/index.css': rd(os.path.join(ov, 'css/index.css')),
    'css/nav.css': rd(os.path.join(ov, 'css/nav.css')),
    'css/table.css': rd(os.path.join(ov, 'css/table.css')),
    '../share/lang/ko.json': rd(os.path.join(ROOT, 'share/lang/ko.json')),
}
IMG = '../kagerou/share/img/'   # preview/overlay.html 에서 본 상대 경로

FAKE_XHR = """<script>
(function(){var F=%s;
function X(){this.readyState=0;this.status=0;}
X.prototype.open=function(m,u,a){this.u=u.split('?')[0];this.sync=(a===false);};
X.prototype.setRequestHeader=function(){};X.prototype.overrideMimeType=function(){};
X.prototype.send=function(){var s=this,ok=(s.u in F);s.readyState=4;s.status=ok?200:404;
 s.responseText=ok?F[s.u]:'';s.response=s.responseText;if(s.sync)return;
 setTimeout(function(){s.onreadystatechange&&s.onreadystatechange();s.onload&&s.onload();},0);};
window.XMLHttpRequest=X;
try{localStorage.removeItem('kagerou_config')}catch(e){}
})();
</script>""" % json.dumps(files).replace('</', '<\\/')

# kagerou 는 사용자 CSS 안의 var(--graph-height) 를 설정값으로 바꿔 넣는다. 그 값을 흉내 낸다
USER_CSS = """<style>:root{--graph-height:1.5rem}</style>
<script>if(!/nocss/.test(location.search))document.write('<link rel="stylesheet" href="../kagerou-umbra.css?'+Date.now()+'">')</script>"""

FAKE_DATA = r"""<script>
window.addEventListener('load', function() {
  var q = location.search
  if(/standby/.test(q)) return
  var big = /p24/.test(q)
  var roster = [
    ['YOU','Sam'],['Aria Moon','Pct'],['Kaito Ren','Drg'],['Lumi Sol','Blm'],
    ['Brave Heart','War'],['Iron Wall','Pld'],['Sage Leaf','Sge'],['Holy Bell','Whm'],
    ['Night Fox','Nin'],['Red Rose','Rdm'],['Wind Song','Brd'],['Gear Head','Mch'],
    ['Fist Star','Mnk'],['Dark Moon','Drk'],['Gun Blade','Gnb'],['Star Card','Ast'],
    ['Fairy Tale','Sch'],['Scythe Kun','Rpr'],['Twin Fang','Vpr'],['Dance Step','Dnc'],
    ['Summon Me','Smn'],['Blue Mage','Blm'],['Lance Ace','Drg'],['Shield Up','Pld']
  ].slice(0, big ? 24 : 8)
  var c = {}, total = 0
  roster.forEach(function(r, i) { r.push(Math.round(14500 - i * (big ? 420 : 1250) - (i % 3) * 90)); total += r[2] })
  roster.forEach(function(r, i) {
    var heal = ['Whm','Sch','Ast','Sge'].indexOf(r[1]) >= 0
    c[r[0]] = {name:r[0], Job:r[1], encdps:String(r[2]), damage:String(r[2]*222),
      'damage%':Math.round(r[2]/total*100)+'%', swings:String(180+i*7), crithits:String(42+(i*5)%17),
      DirectHitCount:String(55-(i*3)%20), CritDirectHitCount:String(14-(i%9)), misses:'0', deaths:String(i%7===5?1:0),
      enchps:String(heal?9200-i*120:250+i*10), healed:String(heal?2000000:50000), 'healed%':(heal?22:1)+'%',
      OverHealPct:'18%', heals:'60', critheals:'11', damagetaken:String(400000-i*9000), healstaken:'250000',
      ParryPct:'0%', BlockPct:'0%', Last10DPS:'0', Last30DPS:'0', Last60DPS:'0', maxhit:'Skill-30000', MAXHIT:'30000',
      maxhealward:'Heal-20000', MAXHEALWARD:'20000', powerdrain:'0', powerheal:'0', cures:'0', hitfailed:'0'}
  })
  setTimeout(function() {
    document.dispatchEvent(new CustomEvent('onOverlayDataUpdate', {detail: {isActive:'true',
      Encounter:{duration:'08:42', title: big ? '아르카디아 공방전' : '아르카디아',
        CurrentZoneName: big ? '아르카디아 공방전 (24인)' : '아르카디아 선수권: 크루저급 (영웅)',
        encdps:String(total), enchps:'14210'}, Combatant:c}}))
  }, 300)
  setTimeout(function() {
    if(big) document.querySelectorAll('.tabs span')[3].click()
  }, 600)
})
</script>
</body>"""


def inline_script(m):
    t = rd(os.path.normpath(os.path.join(ov, m.group(1).split('?')[0])))
    return '<script>' + t.replace('</script', '<\\/script') + '</script>'


def inline_css(m):
    t = rd(os.path.normpath(os.path.join(ov, m.group(1).split('?')[0])))
    return '<style>' + t.replace("url('../img/", "url('" + IMG) + '</style>'


h = rd(os.path.join(ov, 'index.html'))
h = re.sub(r'<script src="([^"]+)"></script>', inline_script, h)
h = re.sub(r'<link rel="stylesheet" href="((?:css|\.\./share)[^"]+)" />', inline_css, h)
h = h.replace('<meta charset="utf-8" />', '<meta charset="utf-8" />' + FAKE_XHR + USER_CSS, 1)
h = h.replace('</body>', FAKE_DATA, 1)

# ?cols=i.icon,i.name,... 로 DPS 탭의 열 구성을 바꿔 볼 수 있게 함
COLS = """var __cols = /[?&,]cols=([^&]+)/.exec(location.search)
if(__cols) CONFIG_DEFAULT.tabs[0].col = decodeURIComponent(__cols[1]).split(',')
window.config = new Config()"""
assert 'window.config = new Config()' in h
h = h.replace('window.config = new Config()', COLS, 1)

os.makedirs(OUT, exist_ok=True)
with open(os.path.join(OUT, 'overlay.html'), 'w', encoding='utf-8') as f:
    f.write(h)

INDEX = """<!doctype html>
<html lang="ko"><head><meta charset="utf-8"><title>kagerou 미리보기</title>
<style>
  body { margin: 0; padding: 20px; font: 14px 'Malgun Gothic', sans-serif; color: #ddd;
         background: #3b4a55 linear-gradient(135deg, #2c3a44, #56646c 60%, #39444b); min-height: 100vh; box-sizing: border-box; }
  .bar { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-bottom: 16px; }
  .bar button { background: #2a2a2a; color: #ddd; border: 1px solid #444; border-radius: 3px; padding: 4px 12px; cursor: pointer; }
  .bar button.on { border-color: #888; color: #fff; }
  .grid { display: flex; flex-wrap: wrap; gap: 24px; align-items: flex-start; }
  figure { margin: 0; }
  figcaption { margin-bottom: 6px; color: #eee; }
  iframe { border: 0; resize: both; overflow: hidden; display: block; background: transparent; }
</style></head><body>
<div class="bar">
  <button id="umbra" class="on">Umbra 스타일</button>
  <button id="orig">원본</button>
  <button id="reload">새로고침</button>
  <span style="opacity:.7">kagerou-umbra.css 를 고친 뒤 새로고침하면 반영됩니다. 창 오른쪽 아래를 끌어 크기를 바꿀 수 있습니다.</span>
</div>
<div class="grid">
  <figure><figcaption>8인 (DPS 탭)</figcaption><iframe data-q="p8" style="width:520px;height:330px"></iframe></figure>
  <figure><figcaption>24인 (24 탭)</figcaption><iframe data-q="p24" style="width:620px;height:420px"></iframe></figure>
  <figure><figcaption>대기 화면</figcaption><iframe data-q="standby" style="width:520px;height:330px"></iframe></figure>
</div>
<script>
  var css = ''
  var umbra = document.getElementById('umbra'), orig = document.getElementById('orig')
  function load() {
    document.querySelectorAll('iframe').forEach(function(f) { f.src = 'overlay.html?' + f.dataset.q + css + '&t=' + Date.now() })
  }
  umbra.onclick = function() { css = ''; umbra.classList.add('on'); orig.classList.remove('on'); load() }
  orig.onclick = function() { css = ',nocss'; orig.classList.add('on'); umbra.classList.remove('on'); load() }
  document.getElementById('reload').onclick = load
  load()
</script>
</body></html>"""
with open(os.path.join(OUT, 'index.html'), 'w', encoding='utf-8') as f:
    f.write(INDEX)

print('미리보기를 만들었습니다:', os.path.join(OUT, 'index.html'))
