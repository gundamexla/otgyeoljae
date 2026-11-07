using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Moa;
class CompactLive
{
    static int code;
    static Button Send(Control parent) { foreach(Control c in parent.Controls) { var b=c as Button; if(b!=null && b.AccessibleName=="메시지 보내기")return b; var child=Send(c);if(child!=null)return child; }return null; }
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        var state=new MoaStateController{LocalMode=true};
        using(var pet=new MoaPetForm(args[0],state))
        using(var chat=new MoaCompactChat(pet))
        {
            chat.Location=new Point(-20000,-20000);chat.ShowInTaskbar=false;
            chat.Shown+=async delegate {
                try {
                    var box=(TextBox)chat.Controls.Find("CompactInput",true)[0];
                    for(int i=0;i<300 && box.ReadOnly;i++)await Task.Delay(100);
                    if(box.ReadOnly)throw new Exception("History load failed");
                    box.Text="네 이름은 뭐야? 한 문장으로 답해 줘.";Send(chat).PerformClick();
                    for(int i=0;i<1800 && state.Busy;i++)await Task.Delay(100);
                    if(state.Outcome!=Outcome.Completed || !state.Answer.Contains("모아"))throw new Exception("Real compact reply failed: "+state.Notice);
                    var transcript=(RichTextBox)chat.Controls.Find("CompactTranscript",true)[0];
                    if(!transcript.Text.Contains(state.Answer))throw new Exception("Reply not displayed");
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"compact-answer.txt"),state.Answer);
                    using(var image=new Bitmap(chat.Width,chat.Height)){chat.DrawToBitmap(image,new Rectangle(Point.Empty,chat.Size));image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"compact-live.png"),ImageFormat.Png);}
                    Console.WriteLine("PASS actual local AI completes and displays in compact chat");
                }catch(Exception ex){Console.WriteLine("FAIL "+ex.Message);code=1;}finally{chat.Close();}
            };
            Application.Run(chat);
        }
        return code;
    }
}
