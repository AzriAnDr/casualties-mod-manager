using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

// Runtime extension of the three game's handwritten RASTER fonts. Original glyph
// pixels/metrics stay intact. New Cyrillic is our approximation, not author artwork.
internal static class OriginalCyrillic
{
    private const string Russian = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя";
    private static readonly HashSet<TMP_FontAsset> generated = new HashSet<TMP_FontAsset>();
    private static readonly Dictionary<TMP_FontAsset,TMP_FontAsset> cache = new Dictionary<TMP_FontAsset,TMP_FontAsset>();
    private static readonly HashSet<TMP_FontAsset> failed = new HashSet<TMP_FontAsset>();
    internal static bool IsGenerated(TMP_FontAsset font) { return font && generated.Contains(font); }
    internal static bool Supported(TMP_FontAsset font)
    {
        if (!font) return false;
        string name=font.name.ToLowerInvariant();
        return name.Contains("duneorange") || name.Contains("experimentieatcrayons") || name.Contains("milkyonlyyou");
    }
    internal static bool TryCreate(TMP_FontAsset source, ManualLogSource log, out TMP_FontAsset result)
    {
        result=null;
        if (!Supported(source) || failed.Contains(source)) return false;
        if (cache.TryGetValue(source,out result) && result) return true;
        TMP_FontAsset clone=null;Texture2D atlas=null;Material material=null;
        try
        {
            if ((source.atlasRenderMode!=GlyphRenderMode.RASTER && source.atlasRenderMode!=GlyphRenderMode.RASTER_HINTED) || source.atlasTextures==null || source.atlasTextures.Length!=1 || !source.atlasTexture || !source.material || !source.material.shader) throw new InvalidOperationException("Unsupported handwritten atlas or material");
            int oldW=source.atlasTexture.width,oldH=source.atlasTexture.height;
            if(oldW<16 || oldH<16 || oldW>1024 || oldH>1024)throw new InvalidOperationException("Unexpected handwritten atlas dimensions");
            Color32[] original=ReadPixels(source.atlasTexture);
            var designer=new Designer(source,original,oldW,oldH);
            clone=UnityEngine.Object.Instantiate(source);clone.name="CuRussian-OriginalCyrillic-"+source.name;
            var glyphs=new List<Glyph>();var byIndex=new Dictionary<uint,Glyph>();uint next=1;
            foreach(var old in source.glyphTable)
            {
                if(old.atlasIndex!=0)throw new InvalidOperationException("Original font has multiple glyph atlases");
                var copy=new Glyph(old.index,old.metrics,old.glyphRect,old.scale,0);glyphs.Add(copy);byIndex.Add(copy.index,copy);next=Math.Max(next,copy.index+1);
            }
            var characters=new List<TMP_Character>();var present=new HashSet<uint>();
            foreach(var old in source.characterTable)
            {
                Glyph copy;if(!byIndex.TryGetValue(old.glyphIndex,out copy))throw new InvalidOperationException("Original character has no glyph");
                characters.Add(new TMP_Character(old.unicode,clone,copy){scale=old.scale});present.Add(old.unicode);
            }
            const int size=2048;var pixels=new Color32[size*size];
            for(int y=0;y<oldH;y++)Array.Copy(original,y*oldW,pixels,y*size,oldW);
            int x=4,yPos=oldH+4,row=0,added=0;
            string required=BuildRequired();
            foreach(char c in required)
            {
                if(present.Contains(c))continue;
                char donor=Designer.DirectDonor(c);
                TMP_Character originalCharacter;
                if(donor!='\0' && source.characterLookupTable.TryGetValue(donor,out originalCharacter))
                {
                    characters.Add(new TMP_Character(c,clone,byIndex[originalCharacter.glyphIndex]){scale=originalCharacter.scale});present.Add(c);added++;continue;
                }
                Drawn draw=designer.Create(c);
                if(draw.Image.Width<1 || draw.Image.Height<1)throw new InvalidOperationException("Empty custom glyph U+"+((int)c).ToString("X4"));
                if(x+draw.Image.Width+4>size){x=4;yPos+=row+4;row=0;}
                if(yPos+draw.Image.Height+4>size)throw new InvalidOperationException("Custom Cyrillic does not fit the private atlas");
                for(int yy=0;yy<draw.Image.Height;yy++)for(int xx=0;xx<draw.Image.Width;xx++)pixels[(yPos+draw.Image.Height-1-yy)*size+x+xx]=new Color32(255,255,255,draw.Image.Data[yy*draw.Image.Width+xx]);
                var glyph=new Glyph(next++,draw.Metrics,new GlyphRect(x,yPos,draw.Image.Width,draw.Image.Height),1,0);glyphs.Add(glyph);characters.Add(new TMP_Character(c,clone,glyph));present.Add(c);added++;
                x+=draw.Image.Width+4;row=Math.Max(row,draw.Image.Height);
            }
            atlas=new Texture2D(size,size,TextureFormat.RGBA32,false,true);atlas.name=clone.name+" Atlas";atlas.filterMode=source.atlasTexture.filterMode;atlas.wrapMode=TextureWrapMode.Clamp;atlas.SetPixels32(pixels);atlas.Apply(false,false);
            material=new Material(source.material);material.name=clone.name+" Material";material.SetTexture("_MainTex",atlas);
            if(material.HasProperty("_TextureWidth"))material.SetFloat("_TextureWidth",size);if(material.HasProperty("_TextureHeight"))material.SetFloat("_TextureHeight",size);
            Set(clone,"m_GlyphTable",glyphs);Set(clone,"m_CharacterTable",characters);Set(clone,"m_GlyphLookupDictionary",null);Set(clone,"m_CharacterLookupDictionary",null);
            Set(clone,"m_AtlasWidth",size);Set(clone,"m_AtlasHeight",size);Set(clone,"m_AtlasTexture",atlas);Set(clone,"m_AtlasTextureIndex",0);
            clone.atlasTextures=new[]{atlas};clone.material=material;clone.atlasPopulationMode=AtlasPopulationMode.Static;clone.isMultiAtlasTexturesEnabled=false;
            // These three inspected fonts have no alternative typeface entries.
            Set(clone,"m_FontWeightTable",new TMP_FontWeightPair[10]);
            clone.fallbackFontAssetTable=source.fallbackFontAssetTable==null?new List<TMP_FontAsset>():new List<TMP_FontAsset>(source.fallbackFontAssetTable);
            clone.ReadFontAssetDefinition();
            foreach(char c in required){TMP_Character check;if(!clone.characterLookupTable.TryGetValue(c,out check)||check.glyph==null||check.glyph.atlasIndex!=0)throw new InvalidOperationException("Custom character lookup is incomplete");}
            // Preserve every original character's atlas coordinate and metric value.
            foreach(var old in source.characterTable){var check=clone.characterLookupTable[old.unicode];if(check.glyphIndex!=old.glyphIndex || !check.glyph.metrics.Equals(old.glyph.metrics)||!check.glyph.glyphRect.Equals(old.glyph.glyphRect))throw new InvalidOperationException("Original character changed unexpectedly");}
            UnityEngine.Object.DontDestroyOnLoad(clone);UnityEngine.Object.DontDestroyOnLoad(atlas);UnityEngine.Object.DontDestroyOnLoad(material);
            generated.Add(clone);cache[source]=clone;result=clone;
            log.LogInfo("Original handwritten face extended: "+source.name+"; "+added+" custom/alias characters. Original Latin preserved; Cyrillic is a custom approximation.");return true;
        }
        catch(Exception error)
        {
            failed.Add(source);log.LogWarning("Original handwritten extension unavailable for "+source.name+"; coherent pixel fallback retained: "+error.Message);
            if(material)UnityEngine.Object.Destroy(material);if(atlas)UnityEngine.Object.Destroy(atlas);if(clone)UnityEngine.Object.Destroy(clone);result=null;return false;
        }
    }
    internal static Material DisplayMaterial(Material original,TMP_FontAsset target)
    {
        if(!IsGenerated(target)||!target.material||!target.material.shader||target.material.GetTexture("_MainTex")!=target.atlasTexture)throw new InvalidOperationException("Invalid original-font private atlas material");
        var result=new Material(target.material);result.name="CuRussian-OriginalFace-Material";result.hideFlags=HideFlags.HideAndDontSave;
        if(original)
        {
            foreach(string property in new[]{"_FaceColor","_Color","_OutlineColor"})if(original.HasProperty(property)&&result.HasProperty(property))result.SetColor(property,original.GetColor(property));
            foreach(string property in new[]{"_Stencil","_StencilComp","_StencilOp","_StencilReadMask","_StencilWriteMask","_ColorMask","_CullMode","_ZTestMode"})if(original.HasProperty(property)&&result.HasProperty(property))result.SetFloat(property,original.GetFloat(property));
        }
        return result;
    }
    private static void Set(object value,string field,object content){var f=AccessTools.Field(typeof(TMP_FontAsset),field);if(f==null)throw new MissingFieldException(field);f.SetValue(value,content);}
    private static string BuildRequired(){var b=new System.Text.StringBuilder();for(char c=' ';c<='~';c++)b.Append(c);return b.Append(Russian).Append("«»—–−…№\u00a0").ToString();}
    private static Color32[] ReadPixels(Texture2D source)
    {
        try{return source.GetPixels32();}catch(UnityException){}
        RenderTexture previous=RenderTexture.active,temp=null;Texture2D readable=null;
        try{temp=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);Graphics.Blit(source,temp);RenderTexture.active=temp;readable=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false,true);readable.ReadPixels(new Rect(0,0,source.width,source.height),0,0);readable.Apply();return readable.GetPixels32();}
        finally{RenderTexture.active=previous;if(temp)RenderTexture.ReleaseTemporary(temp);if(readable)UnityEngine.Object.Destroy(readable);}
    }
    private sealed class Raster
    {
        internal int Width,Height;internal byte[] Data;
        internal Raster(int w,int h){Width=Math.Max(1,w);Height=Math.Max(1,h);Data=new byte[Width*Height];}
        internal Raster Resize(int w,int h,bool flipX=false,bool flipY=false)
        {
            var r=new Raster(w,h);for(int y=0;y<r.Height;y++)for(int x=0;x<r.Width;x++){float sx=(x+.5f)*Width/r.Width-.5f,sy=(y+.5f)*Height/r.Height-.5f;if(flipX)sx=Width-1-sx;if(flipY)sy=Height-1-sy;int x0=Mathf.Clamp((int)Math.Floor(sx),0,Width-1),y0=Mathf.Clamp((int)Math.Floor(sy),0,Height-1),x1=Math.Min(x0+1,Width-1),y1=Math.Min(y0+1,Height-1);float fx=Mathf.Clamp01(sx-x0),fy=Mathf.Clamp01(sy-y0);r.Data[y*r.Width+x]=(byte)Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(Data[y0*Width+x0],Data[y0*Width+x1],fx),Mathf.Lerp(Data[y1*Width+x0],Data[y1*Width+x1],fx),fy));}return r;
        }
    }
    private sealed class Drawn{internal Raster Image;internal GlyphMetrics Metrics;internal Drawn(Raster image,GlyphMetrics metrics){Image=image;Metrics=metrics;}}
    private sealed class Designer
    {
        private readonly TMP_FontAsset font;private readonly Color32[] pixels;private readonly int atlasW,atlasH,cap,xheight,baseline,canvasHeight,stroke,spacing;
        private Raster canvas,brush;private float left,top,width,height;
        internal Designer(TMP_FontAsset source,Color32[] data,int w,int h)
        {
            font=source;pixels=data;atlasW=w;atlasH=h;cap=Math.Max(30,Mathf.RoundToInt(source.faceInfo.capLine));xheight=Math.Max(20,Mathf.RoundToInt(source.faceInfo.meanLine));baseline=Mathf.RoundToInt(cap*1.45f);canvasHeight=Mathf.RoundToInt(cap*1.95f);stroke=MeasureStroke();spacing=Math.Max(3,Mathf.RoundToInt(cap*.08f));brush=MakeBrush();
        }
        internal static char DirectDonor(char c)
        {
            const string russian="АВЕКМНОРСТХаеокрсутдпх";const string latin="ABEKMHOPCTXaeokpcymgnx";
            if(c=='\u00a0')return ' ';int i=russian.IndexOf(c);return i>=0?latin[i]:'\0';
        }
        private Drawn Original(char c)
        {
            TMP_Character character;if(!font.characterLookupTable.TryGetValue(c,out character))throw new InvalidOperationException("Missing source donor "+c);
            Glyph glyph=character.glyph;var rect=glyph.glyphRect;var image=new Raster(rect.width,rect.height);
            for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++){int sx=rect.x+x,sy=rect.y+rect.height-1-y;if(sx>=0&&sy>=0&&sx<atlasW&&sy<atlasH)image.Data[y*image.Width+x]=pixels[sy*atlasW+sx].a;}
            return new Drawn(image,glyph.metrics);
        }
        private Raster Donor(char c,float h,float w=-1,bool flipX=false,bool flipY=false)
        {
            var raw=Crop(Original(c).Image);int targetH=Math.Max(1,Mathf.RoundToInt(h)),targetW=w<0?Math.Max(1,Mathf.RoundToInt(raw.Width*h/raw.Height)):Math.Max(1,Mathf.RoundToInt(w));return raw.Resize(targetW,targetH,flipX,flipY);
        }
        private static Raster Crop(Raster source)
        {
            int minX=source.Width,minY=source.Height,maxX=-1,maxY=-1;
            for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++)if(source.Data[y*source.Width+x]>0){minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);}
            if(maxX<minX)return new Raster(1,1);var result=new Raster(maxX-minX+1,maxY-minY+1);for(int y=0;y<result.Height;y++)Array.Copy(source.Data,(y+minY)*source.Width+minX,result.Data,y*result.Width,result.Width);return result;
        }
        private int MeasureStroke()
        {
            var reference=Original('H').Image;double ink=0;foreach(byte pixel in reference.Data)ink+=pixel/255.0;
            int measured=(int)Math.Round(ink/(2*reference.Height+reference.Width*.6)*1.5);
            return Math.Max(2,Mathf.Clamp(measured,Mathf.RoundToInt(cap*.09f),Mathf.RoundToInt(cap*.17f)));
        }
        private Raster MakeBrush()
        {
            // Keep a native stem's complete two-dimensional alpha field: uneven
            // width, leaning contour, transparent scratches and original opacity.
            // In Orange Book the lower-case l has a long foot; exclude that foot
            // as a single rectangular crop, never normalize individual rows.
            var raw=Crop(Original('l').Image);
            int length=Math.Max(1,Mathf.RoundToInt(raw.Height*.76f));
            var upper=new Raster(raw.Width,length);
            Array.Copy(raw.Data,upper.Data,upper.Data.Length);
            var stem=Crop(upper);
            return raw.Width>stem.Width*1.6f?stem:raw;
        }
        private void Put(Raster image,float x,float y)
        {
            int xx=Mathf.RoundToInt(x),yy=Mathf.RoundToInt(y);
            for(int dy=0;dy<image.Height;dy++)for(int dx=0;dx<image.Width;dx++){int tx=xx+dx,ty=yy+dy;if(tx<0||ty<0||tx>=canvas.Width||ty>=canvas.Height)continue;int i=ty*canvas.Width+tx;canvas.Data[i]=Math.Max(canvas.Data[i],image.Data[dy*image.Width+dx]);}
        }
        private void Put(Raster image){Put(image,left,top);}
        private void Stroke(params float[] p)
        {
            // Rotate the native raster piece as a whole. Width is inherited from
            // its source metrics, not a distance-field/vector line thickness.
            // No alpha amplification, row recentering or opaque normalization.
            for(int segment=0;segment+3<p.Length;segment+=2)
            {
                float x1=left+p[segment]*width,y1=top+p[segment+1]*height,x2=left+p[segment+2]*width,y2=top+p[segment+3]*height;
                float dx=x2-x1,dy=y2-y1,length=Math.Max(1,(float)Math.Sqrt(dx*dx+dy*dy));
                float scale=Math.Max(.45f,height/cap),pen=brush.Width*scale;
                int minX=Math.Max(0,(int)Math.Floor(Math.Min(x1,x2)-pen)),maxX=Math.Min(canvas.Width-1,(int)Math.Ceiling(Math.Max(x1,x2)+pen)),minY=Math.Max(0,(int)Math.Floor(Math.Min(y1,y2)-pen)),maxY=Math.Min(canvas.Height-1,(int)Math.Ceiling(Math.Max(y1,y2)+pen));
                for(int y=minY;y<=maxY;y++)for(int x=minX;x<=maxX;x++)
                {
                    float along=((x-x1)*dx+(y-y1)*dy)/length,cross=((x-x1)*dy-(y-y1)*dx)/length;
                    if(along<0||along>length)continue;
                    int bx=Mathf.RoundToInt(cross/scale+(brush.Width-1)*.5f);
                    int by=Mathf.RoundToInt(along/length*(brush.Height-1));
                    if(bx<0||bx>=brush.Width||by<0||by>=brush.Height)continue;
                    int index=y*canvas.Width+x;canvas.Data[index]=Math.Max(canvas.Data[index],brush.Data[by*brush.Width+bx]);
                }
            }
        }
        internal Drawn Create(char c)
        {
            bool upper=Char.IsUpper(c);char C=Char.ToUpperInvariant(c);height=upper?cap:xheight;width=Math.Max(stroke*3,Mathf.RoundToInt(height*.76f));left=cap*.15f;top=baseline-height;canvas=new Raster(cap*3,canvasHeight);
            switch(C)
            {
                case 'Б':Put(Donor('b',height,width));Stroke(0,.03f,.92f,.03f);break;
                case 'В':Put(Donor('B',height,width));break;
                case 'Г':Put(Donor('L',height,width,false,true));break;
                case 'Д':Put(Donor('V',height,width,false,true));Stroke(-.06f,1,1.06f,1);Stroke(-.06f,.98f,-.06f,1.17f);Stroke(1.06f,.98f,1.06f,1.17f);break;
                case 'Е':Put(Donor(upper?'E':'e',height,width));break;
                case 'Ё':Put(Donor(upper?'E':'e',height,width));Put(Donor('.',Math.Max(stroke,height*.085f)),left+width*.25f,top-height*.21f);Put(Donor('.',Math.Max(stroke,height*.085f)),left+width*.68f,top-height*.21f);break;
                case 'Ж':Put(Donor('X',height,width*1.2f));width*=1.2f;Stroke(.49f,0,.51f,1);break;
                case 'З':Put(Donor('3',height,width*.9f));break;
                case 'И':case 'Й':
                    Put(Donor('N',height,width,true));
                    if(C=='Й'){var bowl=Donor('U',height,width*.65f);int start=Mathf.RoundToInt(bowl.Height*.65f);var part=new Raster(bowl.Width,bowl.Height-start);Array.Copy(bowl.Data,start*bowl.Width,part.Data,0,part.Data.Length);Put(part,left+width*.18f,top-height*.34f);}break;
                case 'К':Put(Donor('K',height,width));break;
                case 'Л':Put(Donor('V',height,width,false,true));break;
                case 'М':Put(Donor('M',height,width*1.2f));break;
                case 'Н':Put(Donor('H',height,width));break;
                case 'П':Stroke(.02f,1,-.02f,.04f,.98f,0,.96f,1);break;
                case 'У':Put(Donor('Y',height,width));Stroke(.5f,.48f,.38f,1.07f,.15f,1.12f);break;
                case 'Ф':Put(Donor('O',height*.76f,width),left,top+height*.12f);Stroke(.52f,-.08f,.48f,1.15f);break;
                case 'Ц':DrawConnectedTs();break;
                case 'Щ':DrawSh(true);break;
                case 'Ч':Put(Donor('U',height*.66f,width));Stroke(1,.02f,1,1);break;
                case 'Ш':DrawSh(false);break;
                case 'Ъ':case 'Ь':case 'Ы':Put(Donor('b',height,width*.83f));if(C=='Ъ')Stroke(-.23f,0,.04f,0);if(C=='Ы')Stroke(1.14f,0,1.14f,1);break;
                case 'Э':Put(Donor('C',height,width,true));Stroke(.45f,.5f,.98f,.5f);break;
                case 'Ю':Stroke(0,0,0,1);Put(Donor('O',height,width*.9f),left+width*.35f,top);Stroke(0,.5f,.4f,.5f);break;
                case 'Я':Put(Donor('R',height,width,true));break;
                default: Punctuation(c);break;
            }
            int minX=canvas.Width,minY=canvas.Height,maxX=-1,maxY=-1;
            for(int y=0;y<canvas.Height;y++)for(int x=0;x<canvas.Width;x++)if(canvas.Data[y*canvas.Width+x]>0){minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);}
            if(maxX<minX)throw new InvalidOperationException("No custom glyph design for U+"+((int)c).ToString("X4"));
            var mask=new Raster(maxX-minX+1,maxY-minY+1);for(int y=0;y<mask.Height;y++)Array.Copy(canvas.Data,(y+minY)*canvas.Width+minX,mask.Data,y*mask.Width,mask.Width);
            return new Drawn(mask,new GlyphMetrics(mask.Width,mask.Height,0,baseline-minY,mask.Width+spacing));
        }
        private void DrawConnectedTs()
        {
            var body=Donor('U',height,width*1.15f);Put(body);
            // U bottoms differ by face. Start the bottom join on a real opaque
            // donor pixel instead of assuming a coordinate beside the curve.
            int ax=-1,ay=-1;double best=Double.MaxValue;
            for(int y=(int)(body.Height*.65f);y<body.Height;y++)for(int x=0;x<body.Width;x++)
            {
                if(body.Data[y*body.Width+x]<128)continue;
                double dx=x-width*.7f,dy=y-height*.96f,score=dx*dx+dy*dy;
                if(score<best){best=score;ax=x;ay=y;}
            }
            if(ax<0)throw new InvalidOperationException("No native U anchor for custom Cyrillic tail");
            Stroke(ax/width,ay/height,1.1f,ay/height,1.1f,1.2f);
        }
        private void DrawSh(bool tail)
        {
            if(font.name.IndexOf("MilkyOnlyYou",StringComparison.OrdinalIgnoreCase)>=0){DrawMilkySh(tail);return;}
            // Three distinct slightly leaning stems; never a Latin W substitution.
            Stroke(.02f,.01f,0,1);Stroke(.51f,.03f,.49f,1);Stroke(1,0,.98f,1);
            Stroke(0,1,.49f,.99f,.99f,1);
            if(tail)Stroke(.98f,.97f,1.04f,1.22f);
        }
        private void DrawMilkySh(bool tail)
        {
            // Milky l curves away from the geometric endpoint. Locate the real
            // ink of each separately drawn stem and bridge it with its own T bar.
            var combined=canvas;var anchorsX=new float[3];var anchorsY=new float[3];
            for(int i=0;i<3;i++)
            {
                canvas=new Raster(combined.Width,combined.Height);
                float at=i*.5f;Stroke(at+.02f,.01f,at,1);
                double best=Double.MaxValue;int ax=-1,ay=-1;
                for(int y=Math.Max(0,(int)(top+height*.8f));y<canvas.Height;y++)for(int x=0;x<canvas.Width;x++)
                {
                    if(canvas.Data[y*canvas.Width+x]<160)continue;
                    double dx=x-(left+at*width),dy=y-(top+height*.95f),score=dx*dx*.1+dy*dy;
                    if(score<best){best=score;ax=x;ay=y;}
                }
                if(ax<0)throw new InvalidOperationException("No native Milky stem anchor");
                anchorsX[i]=ax;anchorsY[i]=ay;
                for(int j=0;j<combined.Data.Length;j++)combined.Data[j]=Math.Max(combined.Data[j],canvas.Data[j]);
            }
            canvas=combined;
            var native=Crop(Original('T').Image);int barHeight=Math.Max(1,Mathf.RoundToInt(native.Height*.2f));
            var piece=new Raster(native.Width,barHeight);Array.Copy(native.Data,piece.Data,piece.Data.Length);piece=Crop(piece);
            float yjoin=(anchorsY[0]+anchorsY[1]+anchorsY[2])/3f,overlap=brush.Width*height/cap*.25f;
            var bar=piece.Resize(Math.Max(1,Mathf.RoundToInt(anchorsX[2]-anchorsX[0]+overlap*2)),Math.Max(1,Mathf.RoundToInt(piece.Height*height/cap)));
            Put(bar,anchorsX[0]-overlap,yjoin-bar.Height*.5f);
            if(tail)Stroke((anchorsX[2]-left)/width,(yjoin-top)/height,(anchorsX[2]-left)/width+.05f,1.22f);
        }
        private void Punctuation(char c)
        {
            switch(c)
            {
                case '!':width=height*.32f;Stroke(.5f,0,.5f,.68f);Put(Donor('.',Math.Max(stroke,height*.09f)),left+width*.32f,top+height*.9f);break;
                case '?':width=height*.65f;Stroke(.08f,.2f,.22f,.04f,.66f,0,.96f,.15f,.86f,.35f,.52f,.51f,.5f,.69f);Put(Donor('.',Math.Max(stroke,height*.09f)),left+width*.43f,top+height*.9f);break;
                case ',':width=height*.32f;Put(Donor('.',Math.Max(stroke,height*.1f)),left+width*.4f,baseline-height*.12f);Stroke(.5f,.94f,.2f,1.15f);break;
                case '\'':case '`':width=height*.32f;Put(Donor('.',Math.Max(stroke,height*.13f)));Stroke(.6f,.06f,.3f,.22f);break;
                case '—':case '–':case '−':case '-':Put(Donor('-',Math.Max(stroke,height*.08f),height*(c=='—'?1.05f:.65f)),left,top+height*.52f);break;
                case '…':for(int i=0;i<3;i++)Put(Donor('.',Math.Max(stroke,height*.1f)),left+i*height*.23f,baseline-height*.1f);break;
                case '№':Put(Donor('N',height,width));Put(Donor('o',height*.35f,width*.4f),left+width*1.06f,top);Stroke(1.06f,.46f,1.46f,.46f);break;
                case '«':Stroke(.47f,.2f,.12f,.5f,.47f,.8f);Stroke(.97f,.2f,.62f,.5f,.97f,.8f);break;
                case '»':Stroke(.12f,.2f,.47f,.5f,.12f,.8f);Stroke(.62f,.2f,.97f,.5f,.62f,.8f);break;
                case '#':Stroke(.3f,.04f,.18f,1);Stroke(.8f,0,.68f,.95f);Stroke(.02f,.34f,1,.34f);Stroke(0,.7f,.98f,.7f);break;
                case '$':Put(Donor('S',height,width));Stroke(.5f,-.1f,.5f,1.1f);break;
                case '%':Put(Donor('o',height*.3f,width*.4f));Put(Donor('o',height*.3f,width*.4f),left+width*.6f,top+height*.7f);Stroke(.12f,.95f,.9f,.02f);break;
                case '*':Stroke(.1f,.25f,.9f,.75f);Stroke(.1f,.75f,.9f,.25f);Stroke(.5f,.1f,.5f,.9f);break;
                case '+':Stroke(.1f,.5f,.9f,.5f);Stroke(.5f,.1f,.5f,.9f);break;
                case '/':Stroke(.05f,1,.95f,0);break;
                case '\\':Stroke(.05f,0,.95f,1);break;
                case '<':Stroke(.9f,.05f,.1f,.5f,.9f,.95f);break;
                case '>':Stroke(.1f,.05f,.9f,.5f,.1f,.95f);break;
                case '=':Stroke(.1f,.35f,.9f,.35f);Stroke(.1f,.65f,.9f,.65f);break;
                case '^':Stroke(.1f,.3f,.5f,0,.9f,.3f);break;
                case '_':Stroke(0,1,1,1);break;
                case '|':Stroke(.5f,0,.5f,1);break;
                case '{':Stroke(.9f,0,.5f,.05f,.4f,.4f,.1f,.5f,.4f,.6f,.5f,.95f,.9f,1);break;
                case '}':Stroke(.1f,0,.5f,.05f,.6f,.4f,.9f,.5f,.6f,.6f,.5f,.95f,.1f,1);break;
                case '~':Stroke(0,.56f,.25f,.4f,.7f,.6f,1,.44f);break;
                default:throw new InvalidOperationException("No custom punctuation recipe U+"+((int)c).ToString("X4"));
            }
        }
    }
}
