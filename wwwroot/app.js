'use strict';

const { createApp } = Vue;
const zone = 'Europe/Minsk';
const dateFormatter = new Intl.DateTimeFormat('en-CA', {timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit'});
const dateObject = value => new Date(value + 'T12:00:00Z');
const iso = date => date.toISOString().slice(0, 10);
const addDays = (value, days) => iso(new Date(dateObject(value).getTime() + days * 86400000));
const monday = value => addDays(value, -((dateObject(value).getUTCDay() + 6) % 7));
const format = (value, options) => new Intl.DateTimeFormat('ru', {timeZone: zone, ...options}).format(dateObject(value));
const timestamp = value => new Intl.DateTimeFormat('ru', {timeZone: zone, day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit'}).format(new Date(value));
const roles = {student:'Учащийся', curator:'Куратор', teacher:'Преподаватель', deputy:'Заместитель директора',
    academic:'Календарь недель (зам. по учебной работе)', admin:'Администрация', sysadmin:'Системный администратор'};
const staffRoleNames = Object.fromEntries(Object.entries(roles).filter(([role]) => role !== 'student'));
const icons = {
    calendar:'<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M16 3v4M8 3v4M3 10h18"/>',
    book:'<path d="M12 5v15M12 5C8 2 5 3 2 4v15c3-1 6-2 10 1 4-3 7-2 10-1V4c-3-1-6-2-10 1Z"/>',
    cap:'<path d="m2 8 10-5 10 5-10 5-10-5ZM6 10v6c3 3 9 3 12 0v-6m4-2v7"/>',
    bell:'<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9m-8 13h4"/>',
    chat:'<path d="M21 11a8 8 0 0 1-8 8H4l2-4a8 8 0 1 1 15-4ZM8 11h8"/>',
    users:'<circle cx="9" cy="7" r="4"/><path d="M2 21v-3a5 5 0 0 1 5-5h4a5 5 0 0 1 5 5v3M17 3a4 4 0 0 1 0 8m2 2a5 5 0 0 1 3 5v3"/>',
    clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5h4"/>',
    upload:'<path d="M12 16V3m-5 5 5-5 5 5M4 15v5h16v-5"/>',
    file:'<path d="M6 3h8l4 4v14H6zM14 3v5h4M9 12h6m-6 4h6"/>',
    link:'<path d="M10 13a4 4 0 0 0 6 0l3-3a4 4 0 0 0-6-6l-2 2m3 5a4 4 0 0 0-6 0l-3 3a4 4 0 0 0 6 6l2-2"/>'
};
const emptySchedule = () => ({days: [], bells: [], updated: ''});
const todayInMinsk = () => dateFormatter.format(new Date());

const app = createApp({
    data() {
        const today = todayInMinsk();
        return {
            me:null, busy:false, error:'', notice:'', profile:false, page:'schedule', credentials:{loginName:'',password:''},
            roleNames:roles, staffRoleNames, groups:[], groupId:0, schedule:emptySchedule(), today, selected:today,
            view:'week',
            posts:[], newsId:'', postForm:null, topicFilter:'', newsSearch:'', newsStatus:'published', newsLimit:10, savingPost:false, subjects:[], studentSubjectId:'', subjectDetail:null, subjectForm:null, materialAccess:null, movingMaterial:false, newMaterialAccess:{restricted:false,groups:[]},
            staff:[], staffForm:null, staffSearch:'', staffRole:'', staffStatus:'active', peer:0, messages:[], task:false, message:'', deadline:'',
            chatSearch:'', chatDrafts:{}, sendingMessage:false, chatLoading:false, chatRequest:0,
            managedId:0, groupView:'mine', groupSearch:'', studentSearch:'', curatorChoices:{}, groupForm:null, groupConflict:false, students:[], studentForm:null,
            packages:[], importTab:'packages', packageKind:'', packageId:0, preview:null, previewSearch:'', errorsOnly:false, reviewPage:1, lessonForm:null,
            calendar:{anchor:today,week:1}, manualDate:today,
            weeks:['Не указан','Над чертой','Под чертой'],
            weekdays:['','Понедельник','Вторник','Среда','Четверг','Пятница','Суббота','Воскресенье'],
            diffLabels:{added:'Добавятся',changed:'Изменятся',removed:'Исчезнут'}
        };
    },
    computed:{
        navigation() {
            const items = [{id:'schedule',label:'Расписание',icon:'calendar'},
                {id:'subjects',label:'Мои предметы',icon:'book'}, {id:'news',label:'Новости',icon:'bell'}];
            if (this.can('curator','admin')) items.push({id:'groups',label:'Учебные группы',icon:'cap'});
            if (this.can('admin','academic')) items.push({id:'imports',label:'Публикация расписания',icon:'upload'});
            if (this.can(...Object.keys(staffRoleNames))) items.push({id:'chat',label:'Рабочий чат',icon:'chat'});
            if (this.can('admin','sysadmin')) items.push({id:'staff',label:'Сотрудники',icon:'users'});
            return items;
        },
        initials(){return this.me?.name.split(/\s+/).slice(0,2).map(n=>n[0]).join('').toUpperCase();},
        groupTitle(){const g=this.groups.find(g=>g.id===this.groupId); return g?'Группа '+g.number+' · '+g.level:'';},
        scheduleGroups(){return this.groups.filter(g=>this.can('admin')||g.managed);},
        postGroups(){return this.groups.filter(g=>this.can('admin')||g.managed);},
        start(){return monday(this.selected);},
        visibleDays(){
            const dates = Array.from({length:7}, (_,i)=>addDays(this.start,i));
            const count = this.dayLessons(dates[6]).length || this.selected === dates[6] ? 7 :
                this.dayLessons(dates[5]).length || this.selected === dates[5] ? 6 : 5;
            return dates.slice(0,count);
        },
        activeSlots(){
            const dates=this.view==='day'?[this.selected]:this.visibleDays;
            return [...new Set(dates.flatMap(d=>this.dayLessons(d).map(l=>l.slot)))].sort((a,b)=>a-b);
        },
        weekLabel(){const week=this.schedule.days.find(d=>d.date===this.selected)?.week;return week?this.weeks[week]:'';},
        period(){
            if(this.view==='day') return format(this.selected,{weekday:'short',day:'numeric',month:'long',year:'numeric'});
            const last=this.visibleDays.at(-1);
            if(this.start.slice(0,7)===last.slice(0,7))
                return dateObject(this.start).getUTCDate()+'–'+format(last,{day:'numeric',month:'long',year:'numeric'});
            return format(this.start,{day:'numeric',month:'short'})+' — '+format(last,{day:'numeric',month:'short',year:'numeric'});
        },
        topics(){return [...new Set(['Учёба','Мероприятия','Конкурсы','Объявления',...this.posts.map(p=>p.topic)])].sort((a,b)=>a.localeCompare(b,'ru'));},
        newsTopics(){return [...new Set(this.posts.map(post=>post.topic))].sort((a,b)=>a.localeCompare(b,'ru'));},
        filteredPosts(){
            const query=this.newsSearch.trim().toLocaleLowerCase('ru');
            return this.posts.filter(post=>(!this.topicFilter||post.topic===this.topicFilter)&&
                (this.newsStatus==='drafts'?!post.published:!!post.published)&&
                (!query||[post.title,post.body,post.authorName||''].join(' ').toLocaleLowerCase('ru').includes(query)));
        },
        subjectGroups(){return this.groups.filter(g=>this.subjectDetail?.groups.includes(g.id));},
        ownSubject(){return this.can('teacher')&&this.subjectDetail?.subject.owner===this.me.id;},
        peers(){return this.staff.filter(s=>s.id!==this.me.id&&(s.active||s.id===this.peer));},
        chatContacts(){return this.peers.filter(person=>person.name.toLocaleLowerCase('ru').includes(this.chatSearch.trim().toLocaleLowerCase('ru')));},
        chatPeer(){return this.peers.find(person=>person.id===this.peer);},
        canTask(){
            const peer=this.peers.find(p=>p.id===this.peer);
            if(!peer?.active) return false;
            const other=peer.roles.split(',');
            return (this.can('teacher')&&other.includes('teacher'))||(this.can('deputy')&&other.some(r=>['teacher','deputy'].includes(r)));
        },
        myGroups(){return this.groups.filter(group=>group.managed);},
        directoryGroups(){return this.groups.filter(group=>(this.can('admin')||group.available)&&this.matchesGroupSearch(this.groupLabel(group),this.groupSearch));},
        visibleStudents(){return this.students.filter(student=>this.matchesGroupSearch(student.name+' '+student.book,this.studentSearch));},
        managedGroup(){return this.groups.find(g=>g.id===this.managedId&&g.managed);},
        filteredStaff(){return this.editableStaff.filter(person=>
            (!this.staffRole||person.roles.split(',').includes(this.staffRole))&&
            (this.staffStatus==='all'||!!person.active===(this.staffStatus==='active'))&&
            (person.name+' '+person.login).toLocaleLowerCase('ru').includes(this.staffSearch.trim().toLocaleLowerCase('ru')));},
        filteredPackages(){return this.packages.filter(p=>!this.packageKind||p.kind===this.packageKind);},
        previewProblemCount(){return (this.preview?.rows||[]).filter(r=>r.issue||!r.groupId||this.preview.conflicts[r.id]).length;},
        editableStaff(){return this.staff.filter(s=>this.can('sysadmin')||!s.roles.split(',').includes('sysadmin'));},
        assignableRoles(){return Object.entries(this.staffRoleNames).filter(([role])=>role!=='sysadmin'||this.can('sysadmin'));},
        activeCurators(){return this.staff.filter(s=>s.active&&s.roles.split(',').includes('curator'));},
        unmapped(){
            const entries=(this.preview?.rows||[]).filter(r=>!r.groupId);
            return [...new Map(entries.map(r=>[r.level+'|'+r.groupNumber,r])).values()];
        },
        weekMismatch(){return this.preview?.week&&this.preview.package.week&&this.preview.package.week!==this.preview.week;},
        previewRows(){
            const needle=this.previewSearch.trim().toLocaleLowerCase('ru');
            const conflicts=this.preview?.conflicts||{};
            return (this.preview?.rows||[]).filter(r=>(!this.errorsOnly||r.issue||!r.groupId||conflicts[r.id])&&
                (!needle||[r.groupNumber,r.subject,r.source].some(v=>v.toLocaleLowerCase('ru').includes(needle))));
        },
        reviewById(){return new Map((this.preview?.rows||[]).map(r=>[r.id,r]));},
        reviewPages(){return Math.max(1,Math.ceil(this.previewRows.length/40));}
    },
    watch:{
        newsSearch(){this.newsLimit=10;},
        newsStatus(){this.newsLimit=10;},
        topicFilter(){this.newsLimit=10;},
        start(){if(this.me&&this.page==='schedule') this.loadSchedule();},
        previewSearch(){this.reviewPage=1;},
        errorsOnly(){this.reviewPage=1;},
        reviewPages(max){this.reviewPage=Math.min(this.reviewPage,max);}
    },
    async mounted(){
        window.addEventListener('hashchange',()=>this.route());
        this.chatTimer=setInterval(()=>{if(this.me&&this.page==='chat'&&this.peer&&!document.hidden) this.refreshChat();},5000);
        try { this.me=await this.api('/me'); await this.initialize(); }
        catch (e) { if(e.status!==401) this.error=e.message; }
    },
    beforeUnmount(){clearInterval(this.chatTimer);},
    methods:{
        format,timestamp,
        can(...required){return required.some(role=>this.me?.roles.includes(role));},
        groupLabel(g){return g.number+' · '+g.level+' · '+g.year;},
        async api(path, method='GET', body){
            const form=body instanceof FormData;
            const response=await fetch('/api'+path,{method,body:body===undefined?undefined:form?body:JSON.stringify(body),
                headers:body!==undefined&&!form?{'Content-Type':'application/json'}:undefined});
            const content=response.headers.get('Content-Type')||'';
            if(!response.ok){
                const data=content.includes('json')?await response.json():{};
                const error=new Error(data.error||(response.status===401?'Войдите в портал.':response.status===403?'Доступ ограничен.':'Не удалось выполнить запрос.'));
                error.status=response.status;
                throw error;
            }
            return content.includes('json')?response.json():undefined;
        },
        async run(operation){
            this.error='';this.notice='';
            try {return await operation();}
            catch(e){this.error=e.message;if(e.status===401&&this.me) this.me=null;}
        },
        async initialize(){
            await this.loadGroups();
            this.groupId=this.me.groupId||this.scheduleGroups[0]?.id||0;
            await this.route();
        },
        async login(){
            this.busy=true;
            try {await this.run(async()=>{
                await this.api('/login','POST',this.credentials);
                this.credentials.password='';
                this.me=await this.api('/me');
                await this.initialize();
            });}
            finally{this.busy=false;}
        },
        logout(){return this.run(async()=>{
            await this.api('/logout','POST'); this.me=null; this.profile=false; this.schedule=emptySchedule();
            this.preview=null; this.subjectDetail=null; this.subjectForm=null; this.postForm=null;
            this.staffForm=null; this.groupForm=null; this.studentForm=null; this.lessonForm=null;
            this.peer=0;this.chatDrafts={};this.chatSearch='';this.messages=[];this.message='';this.task=false;this.managedId=0;this.groupView='mine';this.groupSearch='';this.studentSearch='';this.students=[];this.staff=[];
            this.packageId=0;this.packages=[];this.posts=[];this.newsId='';this.topicFilter='';this.newsSearch='';this.newsStatus='published';this.subjects=[];this.groups=[];
        });},
        navigate(page){if(location.hash==='#'+page) this.route(); else location.hash=page;},
        route(){
            if(!this.me) return;
            return this.run(async()=>{
                const [page,id]=location.hash.slice(1).split('/');
                this.page=this.navigation.some(n=>n.id===page)?page:'schedule';
                if(this.page==='schedule') await this.fetchSchedule();
                if(this.page==='news'){
                    this.newsId=id||'';
                    this.postForm=null;
                    await this.loadPosts(id);
                }
                if(this.page==='subjects'){
                    if(this.can('student')){
                        this.studentSubjectId=id||'';
                        this.subjectDetail=null;
                    }
                    if(this.can('student')&&id){
                        const detail=await this.api('/subjects/'+encodeURIComponent(id));
                        if(location.hash==='#subjects/'+id) this.subjectDetail=detail;
                    }else this.subjects=await this.api('/subjects');
                }
                if(this.page==='groups'){
                    await this.loadGroups();
                    if(this.can('admin')) this.staff=await this.api('/staff');
                    if(!this.can('curator')) this.groupView='directory';
                    if(this.groupView==='mine'&&this.myGroups.length){
                        this.managedId=this.managedGroup?.id||this.myGroups[0].id;
                        await this.loadStudents();
                    }
                }
                if(['staff','chat'].includes(this.page)) this.staff=await this.api('/staff');
                if(this.page==='imports'){
                    this.packages=await this.api('/packages');
                    this.calendar=await this.api('/calendar')||this.calendar;
                }
            });
        },
        async loadGroups(){
            this.groups=await this.api('/groups');
            this.curatorChoices=Object.fromEntries(this.groups.map(g=>[g.id,g.curatorId||0]));
        },
        assignCurator(group,curatorId){
            return this.run(async()=>{
                try {await this.api('/groups/'+group.id+'/curator','PUT',{curatorId});}
                finally {await this.loadGroups();}
                if(!this.managedGroup){this.managedId=0;this.students=[];this.studentForm=null;this.groupForm=null;}
                if(!this.me.groupId&&!this.scheduleGroups.some(g=>g.id===this.groupId))
                    this.groupId=this.scheduleGroups[0]?.id||0;
                this.notice='Куратор закреплён за группой.';
            });
        },
        dayLessons(date){return this.schedule.days.find(d=>d.date===date)?.lessons||[];},
        bell(slot){
            if(slot===0) return ['Весь день',''];
            const bell=this.schedule.bells.find(b=>b.slot===slot);
            return bell?[bell.start,bell.end]:['Урок '+slot,'Время не задано'];
        },
        move(direction){this.selected=addDays(this.selected,direction*(this.view==='day'?1:7));},
        goToday(){this.today=todayInMinsk();this.selected=this.today;},
        loadSchedule(){return this.run(()=>this.fetchSchedule());},
        async fetchSchedule(){
            const start=this.start, group=this.groupId;
            if(!group){this.schedule=emptySchedule();return;}
            const data=await this.api('/schedule?from='+start+'&group='+group);
            if(this.start===start&&this.groupId===group) this.schedule=data;
        },
        async loadPosts(id){
            this.posts=[];
            const route=location.hash;
            const posts=id?[await this.api('/posts/'+encodeURIComponent(id))]:await this.api('/posts');
            if(location.hash===route) this.posts=posts;
        },
        copyPostLink(id){return this.run(async()=>{
            await navigator.clipboard.writeText(location.origin+location.pathname+location.search+'#news/'+id);
            this.notice='Ссылка на публикацию скопирована.';
        });},
        editPost(id){
            return this.run(async()=>{
                const p=id?await this.api('/posts/'+id):{id:0,title:'',body:'',topic:'',audience:this.can('admin')?'all':'groups'};
                this.postForm={...p,audience:p.audience.startsWith('roles:')?'roles':p.audience,
                    roles:p.audience.startsWith('roles:')?p.audience.slice(6).split(','):[],groups:p.groups||[],removeFile:false};
                await this.$nextTick();this.$refs.newsDialog?.showModal();
            });
        },
        savePost(event){
            if(this.savingPost) return;
            const form=new FormData(event.target), p=this.postForm;
            for(const key of ['title','body','topic','url']) form.set(key,p[key]||'');
            form.set('audience',p.audience==='roles'?'roles:'+p.roles.join(','):p.audience);
            if(p.audience==='groups') p.groups.forEach(g=>form.append('groups',g));
            form.set('publish',event.submitter?.value==='publish');
            form.set('removeFile',p.removeFile);
            this.savingPost=true;
            return this.run(async()=>{
                await this.api('/posts/'+p.id,'PUT',form);
                this.$refs.newsDialog.close();
                this.newsStatus=event.submitter?.value==='publish'?'published':'drafts';
                this.topicFilter='';this.newsSearch='';this.newsLimit=10;
                this.navigate('news');
            }).finally(()=>{this.savingPost=false;});
        },
        unpublishPost(id){return this.run(async()=>{await this.api('/posts/'+id+'/unpublish','POST');this.navigate('news');});},
        openSubject(id){return this.run(async()=>{this.subjectDetail=await this.api('/subjects/'+id);this.subjectForm=null;this.materialAccess=null;
            this.newMaterialAccess={restricted:false,groups:[]};});},
        async editSubject(detail){
            this.subjectForm=detail?{...detail.subject,groups:[...detail.groups]}:{id:0,title:'',groups:[]};
            this.error='';await this.$nextTick();this.$refs.subjectDialog?.showModal();
        },
        openMaterialDialog(){
            this.newMaterialAccess={restricted:false,groups:[]};
            this.$refs.materialDialog.querySelector('form').reset();
            this.error='';this.$refs.materialDialog.showModal();
        },
        async moveMaterial(material,direction){
            if(this.movingMaterial) return;
            this.movingMaterial=true;
            try {await this.run(async()=>{
                const subjectId=this.subjectDetail.subject.id;
                const path='/subjects/'+subjectId;
                await this.api(path+'/materials/'+material+'/order','PUT',{direction});
                const detail=await this.api(path);
                if(this.subjectDetail?.subject.id===subjectId) this.subjectDetail=detail;
            });}
            finally {this.movingMaterial=false;}
        },
        saveSubject(){return this.run(async()=>{
            const result=await this.api('/subjects/'+this.subjectForm.id,'PUT',this.subjectForm);
            this.subjects=await this.api('/subjects');this.subjectDetail=await this.api('/subjects/'+result.id);
            this.$refs.subjectDialog.close();
            this.newMaterialAccess={restricted:false,groups:[]};
        });},
        async editMaterialAccess(material){
            this.materialAccess={id:material.id,title:material.title,restricted:!!material.restricted,
                groups:this.subjectGroups.filter(g=>material.groups.includes(g.id)).map(g=>g.id)};
            this.error='';await this.$nextTick();this.$refs.materialAccessDialog?.showModal();
        },
        saveMaterialAccess(){return this.run(async()=>{
            const path='/subjects/'+this.subjectDetail.subject.id;
            await this.api(path+'/materials/'+this.materialAccess.id+'/access','PUT',this.materialAccess);
            this.subjectDetail=await this.api(path);this.$refs.materialAccessDialog.close();
        });},
        addMaterial(event){
            const form=event.target;
            return this.run(async()=>{
                const path='/subjects/'+this.subjectDetail.subject.id;
                const data=new FormData(form);
                const access=this.newMaterialAccess;
                data.set('restricted',access.restricted);
                if(access.restricted) access.groups.forEach(id=>data.append('groups',id));
                await this.api(path+'/materials','POST',data);
                this.subjectDetail=await this.api(path);
                this.$refs.materialDialog.close();
            });
        },
        deleteMaterial(id){return this.run(async()=>{
            const path='/subjects/'+this.subjectDetail.subject.id;
            await this.api(path+'/materials/'+id,'DELETE');this.subjectDetail=await this.api(path);
        });},
        contactInitials(name){return name.split(/\s+/).slice(0,2).map(part=>part[0]).join('').toUpperCase();},
        chatTime(value){return new Intl.DateTimeFormat('ru',{timeZone:zone,hour:'2-digit',minute:'2-digit'}).format(new Date(value));},
        chatDay(value){return new Intl.DateTimeFormat('ru',{timeZone:zone,day:'numeric',month:'long',year:'numeric'}).format(new Date(value));},
        selectChat(id){
            if(this.sendingMessage) return;
            if(this.peer) this.chatDrafts[this.peer]={message:this.message,task:this.task,deadline:this.deadline};
            this.peer=id;this.messages=[];this.chatRequest++;
            const draft=this.chatDrafts[id]||{};
            this.message=draft.message||'';this.task=!!draft.task;this.deadline=draft.deadline||'';
            if(id){
                this.chatLoading=true;
                return this.run(()=>this.fetchChat()).finally(()=>{this.chatLoading=false;});
            }
        },
        chatKeydown(event){
            if(event.key==='Enter'&&!event.shiftKey&&!event.isComposing&&this.message.trim()&&(!this.task||!this.canTask||this.deadline)){
                event.preventDefault();event.target.form.requestSubmit();
            }
        },
        async refreshChat(){try {await this.fetchChat();} catch(e){if(e.status===401) this.me=null;}},
        async fetchChat(){
            const peer=this.peer;
            if(!peer) return;
            const request=++this.chatRequest;
            const messages=await this.api('/chat/'+peer);
            if(this.peer!==peer||request!==this.chatRequest) return;
            const list=this.$refs.chatMessages;
            const nearBottom=!list||list.scrollHeight-list.scrollTop-list.clientHeight<80;
            const firstLoad=this.messages.length===0;
            this.messages=messages;
            await this.$nextTick();
            if(firstLoad||nearBottom) this.$refs.chatMessages?.scrollTo(0,this.$refs.chatMessages.scrollHeight);
        },
        sendMessage(){
            if(this.sendingMessage||!this.message.trim()) return;
            this.sendingMessage=true;
            const peer=this.peer;
            return this.run(async()=>{
                await this.api('/chat/'+peer,'POST',{body:this.message,deadline:this.canTask&&this.task?this.deadline:null});
                this.message='';this.task=false;this.deadline='';delete this.chatDrafts[peer];
                await this.fetchChat();
                await this.$nextTick();
                this.$refs.chatMessages?.scrollTo(0,this.$refs.chatMessages.scrollHeight);
                this.$refs.chatInput?.focus();
            }).finally(()=>{this.sendingMessage=false;});
        },
        taskStatus(id,status){return this.run(async()=>{await this.api('/tasks/'+id,'PUT',{status});await this.fetchChat();});},
        editGroup(g){this.groupConflict=false;this.groupForm=g?{...g,confirm:false}:{id:0,number:'',level:'ПТО',year:dateObject(this.today).getUTCFullYear(),confirm:false};},
        matchesGroupSearch(value, query){return value.toLocaleLowerCase('ru').includes(query.trim().toLocaleLowerCase('ru'));},
        showGroupView(view){
            this.groupView=view;this.groupForm=null;this.studentForm=null;
            if(view==='mine'&&this.myGroups.length) this.openGroup(this.managedGroup?.id||this.myGroups[0].id);
        },
        openGroup(id){return this.run(async()=>{
            this.managedId=id;this.groupForm=null;this.studentForm=null;this.studentSearch='';this.students=[];
            if(id) await this.loadStudents();
        });},
        async loadStudents(){
            const id=this.managedId;
            const students=await this.api('/groups/'+id+'/students');
            if(this.managedId===id) this.students=students;
        },
        saveGroup(){return this.run(async()=>{
            try {
                const result=await this.api('/groups/'+this.groupForm.id,'PUT',this.groupForm);
                this.managedId=result.id;this.groupView='mine';this.studentSearch='';this.groupForm=null;await this.loadGroups();await this.loadStudents();
            } catch(e){if(e.status===409) this.groupConflict=true;throw e;}
        });},
        editStudent(s){this.studentForm=s?{...s,password:''}:{id:0,name:'',book:'',subgroup:null,password:''};},
        saveStudent(){return this.run(async()=>{
            const s=this.studentForm;
            await this.api('/groups/'+this.managedId+'/students/'+s.id,'PUT',{...s,password:s.password||null});
            this.studentForm=null;await this.loadStudents();
        });},
        deactivate(s){
            if(!confirm('Деактивировать учётную запись учащегося '+s.name+'?')) return;
            return this.run(async()=>{await this.api('/groups/'+this.managedId+'/students/'+s.id,'DELETE');await this.loadStudents();});
        },
        async editStaff(s){
            this.staffForm=s?{...s,password:'',active:!!s.active,roles:s.roles.split(',')}:
                {id:0,name:'',login:'',password:'',roles:[],active:true};
            this.error='';await this.$nextTick();this.$refs.staffDialog.showModal();
        },
        saveStaff(){return this.run(async()=>{
            const self=this.staffForm.id===this.me.id;
            await this.api('/staff/'+this.staffForm.id,'PUT',this.staffForm);
            this.$refs.staffDialog.close();
            if(self){this.me=null;return;}
            this.staff=await this.api('/staff');
        });},
        saveCalendar(){return this.run(async()=>{
            await this.api('/calendar','PUT',this.calendar);if(this.preview) await this.fetchPackage();this.notice='Календарь сохранён.';
        });},
        async selectPackage(id){
            this.importTab='packages';this.packages=await this.api('/packages');this.packageId=id;this.previewSearch='';this.errorsOnly=false;
            this.reviewPage=1;this.lessonForm=null;await this.fetchPackage();
        },
        async fetchPackage(){this.preview=this.packageId?await this.api('/packages/'+this.packageId):null;},
        editPackage(id){return this.run(async()=>{
            const draft=await this.api('/packages/'+id+'/edit','POST');
            await this.selectPackage(draft.id);
            this.notice='Открыт черновик. После правок опубликуйте пакет.';
        });},
        deletePackage(packageItem){
            const effect=packageItem.published
                ? packageItem.kind==='base'
                    ? ' Действующее базовое расписание перестанет отображаться.'
                    : ' Замены на эту дату перестанут действовать, отобразятся базовые занятия.'
                : '';
            if(!confirm('Удалить пакет №'+packageItem.id+' вместе со всеми его записями?'+effect)) return;
            return this.run(async()=>{
                await this.api('/packages/'+packageItem.id,'DELETE');
                this.packages=await this.api('/packages');
                if(this.packageId===packageItem.id){this.preview=null;this.packageId=0;this.lessonForm=null;}
                this.notice='Пакет удалён.';
            });
        },
        loadPackage(){return this.run(async()=>{this.lessonForm=null;this.reviewPage=1;await this.fetchPackage();});},
        async uploadPackage(event){
            const form=event.target; this.busy=true;
            try {await this.run(async()=>{
                const result=await this.api('/packages','POST',new FormData(form));
                await this.loadGroups();await this.selectPackage(result.id);form.reset();
            });}
            finally{this.busy=false;}
        },
        manualPackage(){return this.run(async()=>{
            const result=await this.api('/packages/manual','POST',{anchor:this.manualDate,week:0});
            await this.selectPackage(result.id);this.editLesson();
        });},
        setLessonWeek(r,week){
            return this.run(async()=>{
                await this.api('/packages/'+this.packageId+'/lessons/'+r.id+'/week','PUT',{anchor:this.preview.package.starts,week:Number(week)});
                await this.fetchPackage();
            });
        },
        mapGroup(r,id){
            if(!Number(id)) return;
            return this.run(async()=>{
                await this.api('/packages/'+this.packageId+'/groups','PUT',{number:r.groupNumber,level:r.level,groupId:Number(id)});
                await this.fetchPackage();
            });
        },
        async editLesson(r){
            this.lessonForm=r?{...r,groupId:r.groupId||0}:{id:0,groupId:0,day:1,slot:1,subgroup:0,week:0,status:'lesson',subject:'',teacher:'',room:''};
            this.error='';await this.$nextTick();this.$refs.lessonDialog.showModal();
        },
        saveLesson(){return this.run(async()=>{
            await this.api('/packages/'+this.packageId+'/lessons/'+this.lessonForm.id,'PUT',this.lessonForm);
            this.$refs.lessonDialog.close();await this.fetchPackage();
        });},
        removeLesson(r){
            if(!confirm('Удалить запись «'+r.subject+'» из черновика?')) return;
            return this.run(async()=>{await this.api('/packages/'+this.packageId+'/lessons/'+r.id,'DELETE');await this.fetchPackage();});
        },
        publish(visible){
            const question=visible?'Опубликовать пакет расписания?':'Снять пакет с публикации?';
            if(!confirm(question)) return;
            return this.run(async()=>{
                await this.api('/packages/'+this.packageId+(visible?'/publish':'/unpublish'),'POST');
                await this.selectPackage(this.packageId);this.notice=visible?'Пакет опубликован.':'Пакет снят с публикации.';
            });
        }
    }
});
app.component('portal-icon',{
    props:['name'],computed:{drawing(){return icons[this.name]||'';}},
    template:'<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" v-html="drawing"></svg>'
});
app.component('lesson-card',{
    props:['item'],
    template:`<article class="lesson-card" :class="item.status==='lesson'&&item.changed?'changed':item.status">
        <div class="lesson-main">
            <strong>{{item.subject}}</strong>
            <div v-if="item.subgroup || item.status!=='lesson' || item.changed" class="lesson-labels">
                <span v-if="item.status!=='lesson'||item.changed" class="lesson-status">{{item.status==='cancel'?'Отменено':item.status==='practice'?'Практика':'Замена'}}</span>
                <span v-if="item.subgroup" class="lesson-subgroup">{{item.subgroup}} подгр.</span>
            </div>
        </div>
        <details class="lesson-details">
            <summary :aria-label="'Сведения о занятии: '+item.subject">?</summary>
            <div class="lesson-info">
                <span v-if="item.room">Ауд. {{item.room}}</span>
                <span v-if="item.teacher">{{item.teacher}}</span>
                <span v-if="!item.room&&!item.teacher">Кабинет и преподаватель не указаны.</span>
            </div>
        </details>
    </article>`
});
app.component('group-picker',{
    props:['groups','modelValue'],
    emits:['update:modelValue'],
    data(){return {search:''};},
    computed:{
        visibleGroups(){
            const query=this.search.trim().toLocaleLowerCase('ru');
            return this.groups.filter(g=>this.label(g).toLocaleLowerCase('ru').includes(query));
        }
    },
    methods:{
        label(g){return g.number+' · '+g.level+' · '+g.year;},
        toggle(id, checked){
            this.$emit('update:modelValue',checked?[...this.modelValue,id]:this.modelValue.filter(group=>group!==id));
        }
    },
    template:`<fieldset class="group-picker">
        <legend>Группы с доступом</legend>
        <label v-if="groups.length">Поиск группы<input type="search" v-model="search" placeholder="Номер, уровень или год"></label>
        <small v-if="groups.length">Выбрано групп: {{modelValue.length}}</small>
        <div class="group-options" v-if="visibleGroups.length">
            <label v-for="g in visibleGroups" :key="g.id" class="check">
                <input type="checkbox" :checked="modelValue.includes(g.id)" @change="toggle(g.id,$event.target.checked)">{{label(g)}}
            </label>
        </div>
        <p class="muted" v-else>{{groups.length?'Группы по запросу не найдены.':'Нет доступных групп.'}}</p>
    </fieldset>`
});
app.mount('#app');

