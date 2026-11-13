(() => {
    const body = document.body;
    const role = body.dataset.role;
    const page = body.dataset.page;
    const isAdmin = role === 'admin';
    const statusOptions = ['Pending', 'Acknowledged', 'In Progress', 'Resolved', 'Rejected'];
    const priorities = ['Low', 'Normal', 'High'];
    let session;
    let profile;
    let classes = [];
    let reports = [];
    let notifications = [];
    let activeReportId = null;
    let activeGradeId = null;
    let activeSectionId = null;
    let activeFolder = 'grades';
    let showArchivedGrades = false;
    let showArchivedSections = false;
    let classFolderDialogState = null;
    let classViewGradeId = null;
    let showArchivedClassGrades = false;
    let showArchivedClassSections = false;

    const byId = id => document.getElementById(id);
    const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);

    async function request(url, options = {}) {
        const response = await fetch(url, {
            credentials: 'same-origin',
            headers: {
                Accept: 'application/json',
                ...(options.body && !(options.body instanceof FormData)
                    ? { 'Content-Type': 'application/json' }
                    : {}),
                ...options.headers
            },
            ...options
        });
        const payload = await response.json().catch(() => ({}));
        if (response.status === 401) {
            window.location.replace('index.html');
            throw new Error('Your session has expired. Sign in again.');
        }
        if (!response.ok) {
            const details = Array.isArray(payload.errors) ? payload.errors.slice(0, 20).join('\n') : '';
            throw new Error([payload.message || payload.detail, details]
                .filter(Boolean).join('\n') || `The request failed (HTTP ${response.status}).`);
        }
        return payload;
    }

    function showMessage(text, isError = false) {
        const message = byId(isAdmin ? 'admin-global-message' : 'dashboard-message');
        if (!message) return;
        message.textContent = text;
        message.classList.toggle('is-error', isError);
        message.hidden = !text;
    }

    function formatDate(value, withTime = false) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return 'Date unavailable';
        return new Intl.DateTimeFormat(undefined, withTime
            ? { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' }
            : { month: 'short', day: 'numeric', year: 'numeric' }).format(date);
    }

    function statusSlug(status) {
        return String(status || '').toLowerCase().replaceAll(' ', '-').replace(/[^a-z0-9-]/g, '');
    }

    function statusPill(status) {
        return `<span class="status-pill status-${statusSlug(status)}">${escapeHtml(status || 'Pending')}</span>`;
    }

    function updateIdentity() {
        const name = session?.username || 'Student';
        if (byId('profile-name')) byId('profile-name').textContent = name;
        if (byId('menu-name')) byId('menu-name').textContent = name;
        if (byId('profile-avatar')) byId('profile-avatar').textContent = name.trim().charAt(0).toUpperCase() || 'S';
        if (byId('settings-name')) byId('settings-name').value = name;
        const heading = byId('welcome-heading');
        if (heading) heading.textContent = `Welcome back, ${name.split(/\s+/)[0]}.`;
    }

    function updateClassStatus() {
        const element = byId('class-status');
        if (!element || !profile) return;
        const hasClass = profile.gradeLevelId && profile.sectionId;
        element.textContent = hasClass
            ? `Class on file · ${profile.gradeLevel || 'Unavailable grade'} · ${profile.section || 'Unavailable section'}`
            : 'Class details are not set. Reports can still be submitted; add your class in Settings when ready.';
        element.classList.toggle('is-assigned', Boolean(hasClass));
        element.hidden = false;
    }

    function setSettingsSections(gradeId, selectedSectionId = '') {
        const select = byId('settings-section');
        if (!select) return;
        const grade = classes.find(item => String(item.id) === String(gradeId));
        const sections = grade?.sections || [];
        select.innerHTML = grade
            ? `<option value="">Not set</option>${sections.map(item => `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join('')}`
            : '<option value="">Not set</option>';
        select.disabled = !grade || sections.length === 0;
        if (sections.some(item => String(item.id) === String(selectedSectionId)))
            select.value = String(selectedSectionId);
    }

    function populateClassSettings() {
        const gradeSelect = byId('settings-grade');
        const status = byId('class-settings-status');
        if (!gradeSelect || !status || !profile) return;
        gradeSelect.innerHTML = `<option value="">Not set</option>${classes.map(item =>
            `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join('')}`;
        const grade = classes.find(item => item.id === profile.gradeLevelId);
        const section = grade?.sections.find(item => item.id === profile.sectionId);
        if (grade && section) {
            gradeSelect.value = String(grade.id);
            setSettingsSections(grade.id, section.id);
            status.textContent = `Current class: ${grade.name} · ${section.name}. You can change or clear it here.`;
        } else if (profile.gradeLevelId || profile.sectionId) {
            setSettingsSections('');
            status.textContent = `Saved class assignment: ${profile.gradeLevel || 'Grade not set'} · ${profile.section || 'Section not set'}. This option is no longer active; it remains on your account until you save a new choice or clear it.`;
        } else {
            setSettingsSections('');
            status.textContent = 'Class information is incomplete. You can set it now or leave both fields blank. Reports are not blocked.';
        }
    }

    function getStudentReports() {
        const query = byId('report-search')?.value.trim().toLowerCase() || '';
        const status = byId('status-filter')?.value || '';
        const category = byId('category-filter')?.value || '';
        const sort = byId('sort-filter')?.value || 'newest';
        return reports
            .filter(report => !status || report.status === status)
            .filter(report => !category || report.category === category)
            .filter(report => !query || [report.title, report.location, report.category, report.description]
                .some(value => String(value || '').toLowerCase().includes(query)))
            .sort((left, right) => sort === 'oldest'
                ? new Date(left.createdAt) - new Date(right.createdAt)
                : new Date(right.createdAt) - new Date(left.createdAt));
    }

    function reportRow(report, compact = false) {
        return `<button type="button" class="report-row" data-action="open-report" data-report-id="${report.id}" aria-label="View report: ${escapeHtml(report.title)}">
            <span class="report-row-main">
                <span class="report-row-title">${escapeHtml(report.title)}</span>
                <span class="report-row-meta">${escapeHtml(report.category || 'General')} · ${escapeHtml(report.location)}${report.hasPhoto ? ' · Photo attached' : ''}</span>
            </span>
            <span class="report-row-side">${statusPill(report.status)}${compact ? `<span class="report-row-date">${formatDate(report.createdAt)}</span>` : '<span class="report-arrow" aria-hidden="true">→</span>'}</span>
        </button>`;
    }

    function emptyState(title, text) {
        return `<div class="empty-state"><strong>${escapeHtml(title)}</strong><span>${escapeHtml(text)}</span></div>`;
    }

    function renderStudentReports() {
        const counts = {
            total: reports.length,
            pending: reports.filter(item => ['Pending', 'Acknowledged'].includes(item.status)).length,
            active: reports.filter(item => item.status === 'In Progress').length,
            resolved: reports.filter(item => item.status === 'Resolved').length
        };
        for (const [key, value] of Object.entries(counts)) {
            const element = byId(`stat-${key}`);
            if (element) element.textContent = value;
        }
        const recent = byId('recent-reports');
        if (recent) {
            recent.innerHTML = reports.length
                ? reports.slice(0, 4).map(report => reportRow(report, true)).join('')
                : emptyState('No reports yet', 'Use the + button to submit a maintenance report, then follow its progress here.');
        }
        const list = byId('report-grid');
        if (list) {
            const visible = getStudentReports();
            if (byId('visible-report-count'))
                byId('visible-report-count').textContent = `${visible.length} ${visible.length === 1 ? 'report' : 'reports'}`;
            list.innerHTML = visible.length
                ? visible.map(report => reportRow(report)).join('')
                : reports.length
                    ? emptyState('No reports match these filters', 'Try another status or search term.')
                    : emptyState('No reports yet', 'Use the + button to submit a report. Your submissions stay private to your account.');
        }
    }

    function renderNotifications() {
        const list = byId('student-updates');
        if (!list) return;
        const relevant = notifications.filter(item => reports.some(report => report.id === item.reportId));
        list.innerHTML = relevant.length
            ? relevant.slice(0, 5).map(item => `<button class="update-item" type="button" data-action="open-notification" data-notification-id="${item.id}" data-report-id="${item.reportId}">
                <strong>${escapeHtml(item.message)}</strong>
                <time>${formatDate(item.createdAt, true)}</time>
            </button>`).join('')
            : '<p class="empty-state">There are no updates yet. When the maintenance team changes one of your reports, the update will appear here.</p>';
    }

    async function openReport(reportId) {
        const dialog = byId('report-details-dialog');
        const content = byId(isAdmin ? 'admin-report-details' : 'report-details-body');
        if (!dialog || !content) return;
        content.innerHTML = '<p class="loading-state">Loading report details…</p>';
        if (isAdmin) {
            byId('admin-report-form').hidden = true;
            byId('admin-report-error').textContent = '';
        }
        dialog.showModal();
        try {
            const report = await request(`/api/reports/${reportId}`);
            activeReportId = report.id;
            const history = (report.history || []).map(entry => `<li>
                <span class="timeline-dot" aria-hidden="true"></span>
                <div><strong>${escapeHtml(entry.action)}</strong><p>${escapeHtml(entry.details)}</p>
                <time>${formatDate(entry.createdAt, true)}${entry.actor ? ` · ${escapeHtml(entry.actor)}` : ''}</time></div>
            </li>`).join('');
            content.innerHTML = `<p class="detail-kicker">REPORT FI-${String(report.id).padStart(6, '0')}</p>
                <h2 id="details-title">${escapeHtml(report.title)}</h2>
                <div class="detail-meta">${statusPill(report.status)}<span>${escapeHtml(report.category)}</span><span>${escapeHtml(report.location)}</span><span>Submitted ${formatDate(report.createdAt)}</span></div>
                ${isAdmin ? `<section class="detail-block"><h3>Submitted by</h3><p>${escapeHtml(report.reporter || 'Unknown account')} · ${escapeHtml(report.gradeLevel || 'Grade not set')} · ${escapeHtml(report.section || 'Section not set')}</p></section>` : ''}
                <section class="detail-block"><h3>Description</h3><p>${escapeHtml(report.description)}</p></section>
                ${report.publicUpdate ? `<section class="detail-block"><h3>Latest team update</h3><p>${escapeHtml(report.publicUpdate)}</p></section>` : ''}
                ${report.adminNotes && isAdmin ? `<section class="detail-block"><h3>Internal note</h3><p>${escapeHtml(report.adminNotes)}</p></section>` : ''}
                ${report.assignee && isAdmin ? `<section class="detail-block"><h3>Assigned to</h3><p>${escapeHtml(report.assignee)}</p></section>` : ''}
                ${report.photo ? `<section class="detail-block"><h3>Attached photo</h3><img class="detail-photo" src="${escapeHtml(report.photo)}" alt="Photo attached to ${escapeHtml(report.title)}"></section>` : ''}
                <section class="detail-block"><h3>Progress history</h3>${history
                    ? `<ol class="report-timeline">${history}</ol>`
                    : '<p>No status changes have been recorded yet.</p>'}</section>`;
            if (isAdmin) {
                byId('admin-status').value = report.status;
                byId('admin-priority').value = report.priority || 'Normal';
                byId('admin-assignee').value = report.assignee || '';
                byId('admin-public-update').value = report.publicUpdate || '';
                byId('admin-notes').value = report.adminNotes || '';
                byId('admin-report-form').hidden = false;
            }
        } catch (error) {
            content.innerHTML = `<p class="field-error" role="alert">${escapeHtml(error.message)}</p>`;
        }
    }

    async function loadStudentNotifications() {
        try {
            notifications = await request('/api/notifications');
            renderNotifications();
        } catch (error) {
            const list = byId('student-updates');
            if (list) list.innerHTML = `<p class="field-error" role="alert">${escapeHtml(error.message)}</p>`;
        }
    }

    async function loadStudent() {
        [profile, reports] = await Promise.all([request('/api/profile'), request('/api/reports')]);
        classes = profile.grades || [];
        profile = profile.profile;
        updateIdentity();
        updateClassStatus();
        populateClassSettings();
        renderStudentReports();
        await loadStudentNotifications();
    }

    function adminBreadcrumbs() {
        const nav = byId('folder-breadcrumbs');
        if (!nav) return;
        const parts = ['<button type="button" data-folder-level="grades">All grades</button>'];
        if (activeFolder === 'unassigned') {
            parts.push('<span aria-hidden="true">/</span><span aria-current="page">Unassigned reports</span>');
            nav.innerHTML = parts.join('');
            return;
        }
        if (activeGradeId) {
            const grade = classes.find(item => item.id === activeGradeId);
            parts.push('<span aria-hidden="true">/</span>');
            parts.push(activeSectionId
                ? `<button type="button" data-folder-level="sections">${escapeHtml(grade?.name || 'Grade level')}</button>`
                : `<span aria-current="page">${escapeHtml(grade?.name || 'Grade level')}</span>`);
        }
        if (activeSectionId) {
            const grade = classes.find(item => item.id === activeGradeId);
            const section = grade?.sections.find(item => item.id === activeSectionId);
            parts.push('<span aria-hidden="true">/</span>');
            parts.push(activeFolder === 'reports'
                ? `<span aria-current="page">${escapeHtml(section?.name || 'Section')}</span>`
                : `<span aria-current="page">Unassigned reports</span>`);
        }
        nav.innerHTML = parts.join('');
    }

    function folderIconSvg() {
        return '<svg viewBox="0 0 32 26" focusable="false" aria-hidden="true"><path d="M2.5 5.5a2 2 0 0 1 2-2h8l3 3h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-23a2 2 0 0 1-2-2z"></path><path d="M2.5 9h27"></path></svg>';
    }

    function folderMenu(type, id, name, isActive) {
        return `<button class="folder-menu-trigger" type="button" aria-label="Actions for ${escapeHtml(name)}" aria-expanded="false" aria-haspopup="true"><span aria-hidden="true">•••</span></button>
            <div class="folder-action-menu" hidden>
                <button type="button" data-folder-manage="rename" data-folder-type="${type}" data-folder-id="${id}" data-folder-name="${escapeHtml(name)}">Rename</button>
                <button type="button" data-folder-manage="delete" data-folder-type="${type}" data-folder-id="${id}" data-folder-name="${escapeHtml(name)}">Delete</button>
                <button type="button" data-folder-manage="${isActive ? 'archive' : 'restore'}" data-folder-type="${type}" data-folder-id="${id}" data-folder-name="${escapeHtml(name)}">${isActive ? 'Archive' : 'Restore'}</button>
            </div>`;
    }

    function folderCard({ id, name, type, isActive = true, sectionCount, studentCount, reportCount = 0, pendingReportCount = 0, unassignedReportCount = 0 }) {
        const primaryStat = type === 'grade'
            ? `${sectionCount || 0} ${sectionCount === 1 ? 'section' : 'sections'}`
            : `${studentCount || 0} ${studentCount === 1 ? 'student' : 'students'}`;
        const notice = pendingReportCount || unassignedReportCount
            ? `<span class="class-folder-alert">${pendingReportCount ? `${pendingReportCount} pending` : ''}${pendingReportCount && unassignedReportCount ? ' · ' : ''}${unassignedReportCount ? `${unassignedReportCount} unassigned` : ''}</span>`
            : '';
        return `<article class="class-folder${isActive ? '' : ' is-archived'}">
            <button type="button" class="class-folder-open" data-folder-action="${type}" data-folder-id="${id}" aria-label="Open ${escapeHtml(name)} folder">
                <span class="class-folder-visual" aria-hidden="true"><span class="class-folder-tab"></span><span class="class-folder-paper"></span><span class="class-folder-glyph">${folderIconSvg()}</span><span class="class-folder-corner">↗</span></span>
                <span class="class-folder-copy">
                    <span class="class-folder-kind">${type === 'grade' ? 'GRADE LEVEL' : 'SECTION'}${!isActive ? '<span class="class-folder-archived">ARCHIVED</span>' : ''}</span>
                    <strong class="class-folder-name">${escapeHtml(name)}</strong>
                    <span class="class-folder-metrics"><span>${primaryStat}</span><span>${reportCount} ${reportCount === 1 ? 'report' : 'reports'}</span></span>
                    ${notice}
                    <span class="class-folder-open-label">Open folder <span aria-hidden="true">↗</span></span>
                </span>
            </button>
            <div class="folder-card-menu">${folderMenu(type, id, name, isActive)}</div>
        </article>`;
    }

    function classWorkspaceFolderCard(options) {
        return folderCard(options).replace('data-folder-action=', 'data-class-open=');
    }

    function renderClassWorkspaceBreadcrumb(grade) {
        const breadcrumbs = byId('class-workspace-breadcrumb');
        if (!breadcrumbs) return;
        breadcrumbs.innerHTML = grade
            ? `<button type="button" data-class-root>Class Management</button><span aria-hidden="true">/</span><span aria-current="page">${escapeHtml(grade.name)}</span>`
            : '<span aria-current="page">Class Management</span>';
    }

    function classWorkspaceEmpty(title, description, action = 'grade') {
        const gradeEmpty = action === 'grade';
        const eyebrow = action === null
            ? 'CLASS RECORDS'
            : gradeEmpty ? 'START WITH A GRADE LEVEL' : 'THIS GRADE IS READY FOR SECTIONS';
        return `<div class="class-folder-empty">
            <div class="class-empty-art" aria-hidden="true"><span class="class-empty-sheet"></span><span class="class-empty-folder">${folderIconSvg()}</span></div>
            <p class="eyebrow">${eyebrow}</p>
            <h2>${escapeHtml(title)}</h2>
            <p>${escapeHtml(description)}</p>
            ${action ? `<button type="button" class="button button-primary" ${gradeEmpty ? 'data-create-grade' : 'data-create-section'}><span aria-hidden="true">＋</span> ${gradeEmpty ? 'Add Grade Level' : 'Add Section'}</button>` : ''}
        </div>`;
    }

    async function renderClassWorkspace() {
        const workspace = byId('class-folder-workspace');
        if (!workspace) return;
        const heading = byId('class-options-title');
        const lede = byId('class-workspace-lede');
        const summary = byId('class-workspace-summary');
        const grade = classes.find(item => item.id === classViewGradeId) || null;
        byId('add-class-section').hidden = !grade || !grade.isActive;
        document.querySelectorAll('.class-workspace-actions [data-create-grade]').forEach(button => { button.hidden = Boolean(grade); });
        if (!grade) {
            renderClassWorkspaceBreadcrumb(null);
            heading.textContent = 'Class Management';
            if (lede) lede.textContent = 'Manage grade levels and sections for student assignments.';
            workspace.innerHTML = '<p class="loading-state">Loading grade levels…</p>';
            const [gradeReports, unassigned] = await Promise.all([
                request('/api/admin/report-folders/grades?includeArchived=true'),
                request('/api/admin/report-folders/unassigned')
            ]);
            const reportsByGrade = new Map(gradeReports.map(item => [item.id, item]));
            const visible = classes.filter(item => item.isActive !== showArchivedClassGrades);
            const archivedCount = classes.filter(item => !item.isActive).length;
            const activeCount = classes.filter(item => item.isActive).length;
            const sectionCount = classes.filter(item => item.isActive)
                .reduce((total, item) => total + item.sections.filter(section => section.isActive).length, 0);
            const totalReports = gradeReports.reduce((total, item) => total + item.reportCount, 0);
            summary.innerHTML = `<span><strong>${activeCount}</strong> active grade levels</span><span><strong>${sectionCount}</strong> sections</span><span><strong>${totalReports + unassigned.length}</strong> reports</span>`;
            const archiveToggle = archivedCount
                ? `<button type="button" class="class-archive-toggle" data-class-toggle-archived="grades">${showArchivedClassGrades ? '← Back to active grades' : `View archived grades <span>${archivedCount}</span>`}</button>`
                : '';
            workspace.innerHTML = `<div class="class-directory-head"><div><p class="eyebrow">${showArchivedClassGrades ? 'RETAINED CLASS RECORDS' : 'THE SCHOOL DIRECTORY'}</p><h2>${showArchivedClassGrades ? 'Archived grade levels' : 'Grade Levels'}</h2><p>${showArchivedClassGrades ? 'Archived folders stay available for historical student and report records.' : 'Select a grade level to manage its sections.'}</p></div>${archiveToggle}</div>
                ${visible.length
                    ? `<div class="folder-grid class-directory-grid">${visible.map(item => {
                        const counts = reportsByGrade.get(item.id);
                        return classWorkspaceFolderCard({
                            id: item.id, name: item.name, type: 'grade', isActive: item.isActive,
                            sectionCount: counts?.sectionCount ?? item.sections.filter(section => section.isActive).length,
                            reportCount: counts?.reportCount ?? 0,
                            pendingReportCount: counts?.pendingReportCount ?? 0,
                            unassignedReportCount: counts?.unassignedReportCount ?? 0
                        });
                    }).join('')}</div>`
                    : classWorkspaceEmpty(
                        showArchivedClassGrades ? 'No archived grade levels' : 'No grade levels yet',
                        showArchivedClassGrades ? 'Archived grade levels will appear here.' : 'Create the first grade-level folder, or import the school’s class list.',
                        showArchivedClassGrades ? null : 'grade')}
                ${!showArchivedClassGrades && unassigned.length ? `<button type="button" class="class-unassigned-link" data-class-unassigned>Review ${unassigned.length} unassigned ${unassigned.length === 1 ? 'report' : 'reports'} <span aria-hidden="true">→</span></button>` : ''}`;
            return;
        }

        renderClassWorkspaceBreadcrumb(grade);
        heading.textContent = grade.name;
        if (lede) lede.textContent = 'Manage this grade level’s sections and review its report activity.';
        workspace.innerHTML = '<p class="loading-state">Opening grade level…</p>';
        const result = await request(`/api/admin/report-folders/grades/${grade.id}/sections?includeArchived=true`);
        const sections = result.sections.filter(item => item.isActive !== showArchivedClassSections);
        const activeSections = result.sections.filter(item => item.isActive);
        const archivedCount = result.sections.filter(item => !item.isActive).length;
        const totalReports = result.sections.reduce((total, item) => total + item.reportCount, 0);
        const totalStudents = result.sections.reduce((total, item) => total + item.studentCount, 0);
        summary.innerHTML = `<span><strong>${activeSections.length}</strong> active sections</span><span><strong>${totalStudents}</strong> students</span><span><strong>${totalReports}</strong> reports</span>`;
        const archiveToggle = archivedCount
            ? `<button type="button" class="class-archive-toggle" data-class-toggle-archived="sections">${showArchivedClassSections ? '← Back to active sections' : `View archived sections <span>${archivedCount}</span>`}</button>`
            : '';
        workspace.innerHTML = `<section class="class-grade-context">
                <span class="class-grade-seal" aria-hidden="true">${escapeHtml(grade.name.match(/\d+/)?.[0] || 'LI')}</span>
                <div><p class="eyebrow">GRADE LEVEL FOLDER</p><h2>${escapeHtml(grade.name)}</h2><p>${activeSections.length} active ${activeSections.length === 1 ? 'section' : 'sections'} · ${totalReports} ${totalReports === 1 ? 'report' : 'reports'}</p></div>
                <span class="class-grade-index">CLASS DIRECTORY / ${String(grade.id).padStart(2, '0')}</span>
            </section>
            <div class="class-directory-head"><div><p class="eyebrow">${showArchivedClassSections ? 'RETAINED CLASS RECORDS' : 'INSIDE THIS GRADE LEVEL'}</p><h2>${showArchivedClassSections ? 'Archived sections' : 'Sections'}</h2><p>${showArchivedClassSections ? 'Archived sections and their history remain available to administrators.' : 'Open a section to review reports submitted by its students.'}</p></div>${archiveToggle}</div>
            ${sections.length
                ? `<div class="folder-grid class-directory-grid class-section-grid">${sections.map(section => classWorkspaceFolderCard({
                    id: section.id, name: section.name, type: 'section', isActive: section.isActive,
                    studentCount: section.studentCount, reportCount: section.reportCount,
                    pendingReportCount: section.pendingReportCount
                }).replace('data-class-open="section"', `data-class-open="section" data-grade-id="${grade.id}"`)).join('')}</div>`
                : classWorkspaceEmpty(
                    showArchivedClassSections ? 'No archived sections' : 'No sections in this grade yet',
                    showArchivedClassSections ? 'Archived sections will appear here.' : 'Add a section to complete this grade-level folder.',
                    showArchivedClassSections || !grade.isActive ? null : 'section')}
            ${!grade.isActive ? '<p class="archived-parent-note">This grade level is archived. Student dropdowns exclude it, but its sections and report history remain available.</p>' : ''}`;
    }

    function renderAdminReportTable(items) {
        if (!items.length)
            return emptyState('No reports in this folder', 'Reports from students assigned to this section will appear here.');
        return `<table class="admin-report-list"><thead><tr><th>Report</th><th>Submitted by</th><th>Status</th><th>Location · date</th></tr></thead><tbody>
            ${items.map(report => `<tr><td><button type="button" data-action="open-report" data-report-id="${report.id}">${escapeHtml(report.title)}</button></td>
                <td>${escapeHtml(report.reporter || 'Student')}</td><td>${statusPill(report.status)}</td>
                <td>${escapeHtml(report.location)} · ${formatDate(report.createdAt)}</td></tr>`).join('')}
        </tbody></table>`;
    }

    async function loadGradeFolders() {
        activeFolder = 'grades';
        activeGradeId = null;
        activeSectionId = null;
        adminBreadcrumbs();
        byId('folder-toolbar').hidden = true;
        byId('folder-content').innerHTML = '<p class="loading-state">Loading grade folders…</p>';
        const [grades, unassigned] = await Promise.all([
            request('/api/admin/report-folders/grades?includeArchived=true'),
            request('/api/admin/report-folders/unassigned')
        ]);
        const folders = grades.filter(grade => grade.isActive !== showArchivedGrades);
        const archivedCount = grades.filter(grade => !grade.isActive).length;
        const title = showArchivedGrades ? 'Archived grade levels' : 'Grade levels';
        const toggle = archivedCount
            ? `<button class="button button-quiet folder-archive-toggle" type="button" data-toggle-archived="grades" aria-pressed="${showArchivedGrades}">${showArchivedGrades ? 'Show active folders' : `View archived (${archivedCount})`}</button>`
            : '';
        byId('folder-content').innerHTML = `<div class="folder-section-heading"><div><h2>${title}</h2><span class="record-count">${folders.length} ${folders.length === 1 ? 'folder' : 'folders'}</span></div>${toggle}</div>
            ${folders.length ? `<div class="folder-grid">${folders.map(grade => folderCard({
                id: grade.id, name: grade.name, type: 'grade', isActive: grade.isActive,
                sectionCount: grade.sectionCount, reportCount: grade.reportCount,
                pendingReportCount: grade.pendingReportCount, unassignedReportCount: grade.unassignedReportCount
            })).join('')}</div>` : emptyState(showArchivedGrades ? 'No archived grade levels' : 'No grade levels configured', showArchivedGrades ? 'Archived folders will appear here.' : 'Import the school’s official grade levels and sections in Manage Class Options.')}
            <div class="folder-unassigned"><div class="folder-section-heading"><div><h2>Reports outside class folders</h2><span class="record-count">${unassigned.length} ${unassigned.length === 1 ? 'report' : 'reports'}</span></div></div>
            <div class="folder-grid"><article class="folder-card folder-card-utility"><button type="button" class="folder-card-open" data-folder-action="unassigned" data-folder-id="0" aria-label="Open unassigned reports folder"><span class="folder-card-icon">${folderIconSvg()}</span><span class="folder-card-title">Unassigned reports</span><span class="folder-card-stats"><span>Reports without a complete class assignment</span></span><span class="folder-attention">${unassigned.length} ${unassigned.length === 1 ? 'report' : 'reports'}</span><span class="folder-open-hint">Open folder <span aria-hidden="true">↗</span></span></button></article></div></div>`;
        adminBreadcrumbs();
    }

    async function loadSectionFolders(gradeId) {
        activeFolder = 'sections';
        activeGradeId = Number(gradeId);
        activeSectionId = null;
        adminBreadcrumbs();
        byId('folder-toolbar').hidden = true;
        byId('folder-content').innerHTML = '<p class="loading-state">Opening grade folder…</p>';
        const result = await request(`/api/admin/report-folders/grades/${activeGradeId}/sections?includeArchived=true`);
        const grade = classes.find(item => item.id === activeGradeId);
        const sections = result.sections.filter(section => section.isActive !== showArchivedSections);
        const archivedCount = result.sections.filter(section => !section.isActive).length;
        const toggle = archivedCount
            ? `<button class="button button-quiet folder-archive-toggle" type="button" data-toggle-archived="sections" aria-pressed="${showArchivedSections}">${showArchivedSections ? 'Show active folders' : `View archived (${archivedCount})`}</button>`
            : '';
        byId('folder-content').innerHTML = `<div class="folder-section-heading"><div><h2>${escapeHtml(result.gradeLevel)} sections</h2><span class="record-count">${sections.length} ${sections.length === 1 ? 'section' : 'sections'}</span></div>${toggle}</div>
            ${sections.length ? `<div class="folder-grid">${sections.map(section => folderCard({
                id: section.id, name: section.name, type: 'section', isActive: section.isActive,
                studentCount: section.studentCount, reportCount: section.reportCount,
                pendingReportCount: section.pendingReportCount
            })).join('')}</div>` : emptyState(showArchivedSections ? 'No archived sections' : 'No sections configured', showArchivedSections ? 'Archived sections will appear here.' : 'Import or create sections in Manage Class Options.')}
            ${grade && !grade.isActive ? '<p class="archived-parent-note">This grade level is archived. Its sections and report history remain available to administrators, but students cannot select this class.</p>' : ''}`;
        adminBreadcrumbs();
    }

    function folderEndpoint(type, id, action) {
        return type === 'grade'
            ? `/api/admin/class-options/grades/${id}${action === 'status' ? '/status' : action === 'check' ? '/deletion-check' : ''}`
            : `/api/admin/class-options/sections/${id}${action === 'status' ? '/status' : action === 'check' ? '/deletion-check' : ''}`;
    }

    function openClassFolderDialog(state) {
        classFolderDialogState = state;
        const dialog = byId('class-folder-dialog');
        const typeName = state.type === 'grade' ? 'grade level' : 'section';
        const parent = state.type === 'section'
            ? classes.find(grade => grade.sections.some(section => section.id === state.id))
            : null;
        const location = state.type === 'section' ? ` in ${parent?.name || 'its grade level'}` : '';
        const parentField = byId('class-folder-parent-field');
        const nameField = byId('class-folder-name-field');
        const confirmField = byId('class-folder-confirm-field');
        const nameInput = byId('class-folder-name');
        const confirmInput = byId('class-folder-confirm-name');
        const submit = byId('class-folder-submit');
        const mode = state.mode;

        byId('class-folder-error').textContent = '';
        byId('class-folder-dialog-title').textContent = mode === 'create-grade' ? 'Create grade level'
            : mode === 'create-section' ? 'Create section'
                : mode === 'rename' ? `Rename ${typeName}`
                    : mode === 'delete' ? `Delete ${typeName} permanently`
                        : mode === 'archive' ? `Archive ${typeName}`
                            : `Restore ${typeName}`;
        byId('class-folder-dialog-eyebrow').textContent = mode.startsWith('create') ? 'NEW CLASS FOLDER' : 'FOLDER MANAGEMENT';
        parentField.hidden = mode !== 'create-section';
        nameField.hidden = !['create-grade', 'create-section', 'rename'].includes(mode);
        confirmField.hidden = mode !== 'delete';
        nameInput.value = mode === 'rename' ? state.name : '';
        nameInput.maxLength = state.type === 'grade' ? 40 : 80;
        confirmInput.value = '';
        byId('class-folder-required-name').textContent = state.name || '';
        if (mode === 'create-section') {
            const activeGrades = classes.filter(grade => grade.isActive);
            byId('class-folder-parent').innerHTML = activeGrades.map(grade =>
                `<option value="${grade.id}">${escapeHtml(grade.name)}</option>`).join('');
            if (state.gradeId) byId('class-folder-parent').value = String(state.gradeId);
        }

        if (mode === 'create-grade') {
            byId('class-folder-dialog-copy').textContent = 'Create a grade-level folder. It will be available for student assignments as soon as it has an active section.';
            submit.textContent = 'Create grade level';
        } else if (mode === 'create-section') {
            byId('class-folder-dialog-copy').textContent = 'Create a section under an active grade level. Students can select it after it is saved.';
            submit.textContent = 'Create section';
        } else if (mode === 'rename') {
            byId('class-folder-dialog-copy').textContent = `Rename “${state.name}”${location}. Existing student assignments and historical reports remain linked to this folder.`;
            submit.textContent = 'Save name';
        } else if (mode === 'delete') {
            byId('class-folder-dialog-copy').textContent = `Permanently delete “${state.name}”${location}. This is only allowed when no student or report history depends on it. No students or reports will be deleted.`;
            submit.textContent = 'Delete permanently';
        } else if (mode === 'archive') {
            const dependencies = state.dependencies || {};
            const countText = (count, noun) => `${count || 0} ${noun}${count === 1 ? '' : 's'}`;
            const counts = state.type === 'grade'
                ? `${countText(dependencies.sectionCount, 'section')}, ${countText(dependencies.studentCount, 'student')}, and ${countText(dependencies.reportCount, 'report')} are associated.`
                : `${countText(dependencies.studentCount, 'student')} and ${countText(dependencies.reportCount, 'report')} are associated.`;
            byId('class-folder-dialog-copy').textContent = `Archive “${state.name}”${location}. ${counts} Archiving hides this option from student dropdowns and prevents new assignments; existing assignments and historical reports remain intact and accessible to administrators.`;
            submit.textContent = 'Archive folder';
        } else {
            const gradeActive = state.type !== 'section' || parent?.isActive;
            byId('class-folder-dialog-copy').textContent = gradeActive
                ? `Restore “${state.name}”${location}. It will again be available for new student assignments. Historical student and report records remain unchanged.`
                : `Restore “${state.name}”${location}. Its parent grade level is archived, so students will not see it in dropdowns until that grade level is restored. Historical records remain unchanged.`;
            submit.textContent = 'Restore folder';
        }
        dialog.showModal();
        if (mode === 'rename') nameInput.focus();
        else if (mode === 'delete') confirmInput.focus();
        else if (nameField.hidden) submit.focus();
        else nameInput.focus();
    }

    async function prepareFolderManagement(button) {
        const type = button.dataset.folderType;
        const id = Number(button.dataset.folderId);
        const name = button.dataset.folderName;
        const mode = button.dataset.folderManage;
        if (mode === 'rename') {
            openClassFolderDialog({ mode, type, id, name });
            return;
        }
        if (mode === 'archive' || mode === 'delete') {
            const dependencies = await request(folderEndpoint(type, id, 'check'));
            openClassFolderDialog({
                mode: mode === 'delete' && dependencies.canDelete ? 'delete' : 'archive',
                type, id, name, dependencies
            });
            return;
        }
        openClassFolderDialog({ mode: 'restore', type, id, name });
    }

    async function submitClassFolderDialog(event) {
        event.preventDefault();
        const state = classFolderDialogState;
        if (!state) return;
        const submit = byId('class-folder-submit');
        const error = byId('class-folder-error');
        submit.disabled = true;
        error.textContent = '';
        try {
            let result;
            if (state.mode === 'create-grade') {
                result = await request('/api/admin/class-options/grades', {
                    method: 'POST',
                    body: JSON.stringify({ name: byId('class-folder-name').value })
                });
            } else if (state.mode === 'create-section') {
                const gradeId = Number(byId('class-folder-parent').value);
                result = await request(`/api/admin/class-options/grades/${gradeId}/sections`, {
                    method: 'POST',
                    body: JSON.stringify({ name: byId('class-folder-name').value })
                });
            } else if (state.mode === 'rename') {
                result = await request(folderEndpoint(state.type, state.id, ''), {
                    method: 'PUT',
                    body: JSON.stringify({ name: byId('class-folder-name').value })
                });
            } else if (state.mode === 'delete') {
                if (byId('class-folder-confirm-name').value !== state.name)
                    throw new Error('Type the folder name exactly to confirm permanent deletion.');
                result = await request(folderEndpoint(state.type, state.id, ''), { method: 'DELETE' });
            } else {
                const isActive = state.mode === 'restore';
                result = await request(folderEndpoint(state.type, state.id, 'status'), {
                    method: 'PATCH',
                    body: JSON.stringify({ isActive })
                });
            }
            if (state.mode === 'restore' || state.mode === 'delete') {
                if (state.type === 'grade') showArchivedGrades = false;
                else showArchivedSections = false;
            }
            byId('class-folder-dialog').close();
            classFolderDialogState = null;
            showMessage(result.message);
            await loadClassDirectory();
            if (activeFolder === 'grades') await loadGradeFolders();
            else if (activeFolder === 'sections') await loadSectionFolders(activeGradeId);
            else if (activeFolder === 'reports') await loadSectionReports();
            await loadAdminOverview();
        } catch (requestError) {
            error.textContent = requestError.message;
        } finally {
            submit.disabled = false;
        }
    }

    async function loadSectionReports() {
        if (!activeSectionId) return;
        activeFolder = 'reports';
        adminBreadcrumbs();
        byId('folder-toolbar').hidden = false;
        byId('folder-content').innerHTML = '<p class="loading-state">Loading reports…</p>';
        const params = new URLSearchParams({
            search: byId('folder-search').value.trim(),
            status: byId('folder-status').value,
            category: byId('folder-category').value,
            sort: byId('folder-sort').value
        });
        const result = await request(`/api/admin/report-folders/sections/${activeSectionId}/reports?${params}`);
        byId('folder-content').innerHTML = `<div class="folder-section-heading"><h2>${escapeHtml(result.gradeLevel)} · ${escapeHtml(result.section)}</h2><span class="record-count">${result.reports.length} ${result.reports.length === 1 ? 'report' : 'reports'}</span></div>
            ${renderAdminReportTable(result.reports)}`;
    }

    async function loadUnassignedReports() {
        activeFolder = 'unassigned';
        activeGradeId = null;
        activeSectionId = 0;
        adminBreadcrumbs();
        byId('folder-toolbar').hidden = false;
        byId('folder-content').innerHTML = '<p class="loading-state">Loading unassigned reports…</p>';
        const params = new URLSearchParams({
            search: byId('folder-search').value.trim(),
            status: byId('folder-status').value,
            category: byId('folder-category').value,
            sort: byId('folder-sort').value
        });
        const items = await request(`/api/admin/report-folders/unassigned?${params}`);
        byId('folder-content').innerHTML = `<div class="folder-section-heading"><h2>Unassigned reports</h2><span class="record-count">${items.length} ${items.length === 1 ? 'report' : 'reports'}</span></div>
            ${items.length ? `<table class="admin-report-list"><thead><tr><th>Report</th><th>Submitted by</th><th>Class details</th><th>Status · date</th></tr></thead><tbody>
                ${items.map(report => `<tr><td><button type="button" data-action="open-report" data-report-id="${report.id}">${escapeHtml(report.title)}</button></td>
                <td>${escapeHtml(report.reporter)}</td><td>${escapeHtml(report.gradeLevel || 'Grade not set')} · ${escapeHtml(report.section || 'Section not set')}</td>
                <td>${statusPill(report.status)} · ${formatDate(report.createdAt)}</td></tr>`).join('')}
            </tbody></table>` : emptyState('No unassigned reports', 'Reports without a complete grade and section assignment will appear here.')}`;
    }

    async function loadAdminOverview() {
        const data = await request('/api/admin/overview');
        byId('overview-total').textContent = data.total;
        byId('overview-pending').textContent = data.pending + data.acknowledged;
        byId('overview-progress').textContent = data.inProgress;
        byId('overview-resolved').textContent = data.resolved;
        byId('overview-grades').innerHTML = data.reportsByGrade.length
            ? data.reportsByGrade.map(item => `<button class="overview-grade-row" type="button" data-open-grade="${escapeHtml(item.gradeLevel)}"><strong>${escapeHtml(item.gradeLevel)}</strong><span>${item.reportCount} ${item.reportCount === 1 ? 'report' : 'reports'} <span aria-hidden="true">→</span></span></button>`).join('')
            : '<p class="empty-state">Grade-level report totals will appear once reports are submitted by students with class details.</p>';
        byId('overview-activity').innerHTML = data.recentActivity.length
            ? data.recentActivity.map(item => `<li><strong>${escapeHtml(item.action)} · ${escapeHtml(item.reportTitle || `FI-${item.reportId}`)}</strong><p>${escapeHtml(item.details)}</p><small>${escapeHtml(item.actor || 'fixIt')} · ${formatDate(item.createdAt, true)}</small></li>`).join('')
            : '<li class="empty-state">No report activity has been recorded yet.</li>';
    }

    async function loadClassDirectory() {
        const directory = await request('/api/admin/class-options');
        classes = directory.grades || [];
        const list = byId('class-options-list');
        if (list) {
            const activeGrades = classes.filter(grade => grade.isActive);
            list.innerHTML = activeGrades.length
                ? `<div class="class-import-example" role="table" aria-label="Active class options">
                    <div role="row"><strong role="columnheader">Grade Level</strong><strong role="columnheader">Section</strong></div>
                    ${activeGrades.map(grade => {
                        const sections = grade.sections.filter(section => section.isActive);
                        return sections.length
                            ? sections.map(section => `<div role="row"><span role="cell">${escapeHtml(grade.name)}</span><span role="cell">${escapeHtml(section.name)}</span></div>`).join('')
                            : `<div role="row"><span role="cell">${escapeHtml(grade.name)}</span><span role="cell">No active sections</span></div>`;
                    }).join('')}
                </div>`
                : emptyState('No active class options', 'Import a workbook or create a grade level and section to make class options available.');
        }
        await renderClassWorkspace();
        return classes;
    }

    let classImportPreview = null;

    function renderClassImportPreview(preview) {
        const formatNames = entries => entries.length
            ? `<ul>${entries.map(entry => `<li>${escapeHtml(entry)}</li>`).join('')}</ul>`
            : '<p class="label-muted">None</p>';
        const formatSections = entries => formatNames(entries.map(entry =>
            `${entry.gradeLevel} · ${entry.section}`));
        byId('class-import-diff').innerHTML = `
            <div class="import-change-grid">
                <section><h3>Grade levels to add (${preview.gradesToAdd.length})</h3>${formatNames(preview.gradesToAdd)}</section>
                <section><h3>Grade levels to update (${preview.gradesToRestore.length})</h3>${formatNames(preview.gradesToRestore)}</section>
                <section><h3>Grade levels to remove from dropdowns (${preview.gradesToRemove.length})</h3>${formatNames(preview.gradesToRemove)}</section>
                <section><h3>Sections to add (${preview.sectionsToAdd.length})</h3>${formatSections(preview.sectionsToAdd)}</section>
                <section><h3>Sections to update (${preview.sectionsToRestore.length})</h3>${formatSections(preview.sectionsToRestore)}</section>
                <section><h3>Sections to remove from dropdowns (${preview.sectionsToRemove.length})</h3>${formatSections(preview.sectionsToRemove)}</section>
            </div>
            <p class="import-unchanged">${preview.unchangedSections} section options will remain active. Archived choices are removed only from dropdowns; existing assignments and reports are preserved.</p>`;
    }

    function setupClassOptionsImport() {
        byId('class-import-file').addEventListener('change', () => {
            classImportPreview = null;
            byId('class-import-preview').hidden = true;
            byId('class-import-error').textContent = '';
        });
        byId('class-import-form').addEventListener('submit', async event => {
            event.preventDefault();
            const button = byId('preview-class-import');
            byId('class-import-error').textContent = '';
            byId('class-import-confirm-error').textContent = '';
            byId('class-import-preview').hidden = true;
            classImportPreview = null;
            button.disabled = true;
            try {
                const file = byId('class-import-file').files[0];
                if (!file) throw new Error('Choose an .xlsx workbook to preview.');
                const formData = new FormData();
                formData.append('file', file);
                const preview = await request('/api/admin/class-options/import/preview', {
                    method: 'POST',
                    body: formData
                });
                classImportPreview = preview;
                renderClassImportPreview(preview);
                byId('class-import-preview').hidden = false;
            } catch (error) {
                byId('class-import-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('confirm-class-import').addEventListener('click', async event => {
            if (!classImportPreview) return;
            const button = event.currentTarget;
            button.disabled = true;
            byId('class-import-confirm-error').textContent = '';
            try {
                const result = await request('/api/admin/class-options/import', {
                    method: 'POST',
                    body: JSON.stringify({
                        rows: classImportPreview.rows,
                        snapshot: classImportPreview.snapshot
                    })
                });
                classImportPreview = null;
                byId('class-import-preview').hidden = true;
                byId('class-import-form').reset();
                await loadClassDirectory();
                showMessage(result.message);
            } catch (error) {
                byId('class-import-confirm-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('cancel-class-import').addEventListener('click', () => {
            classImportPreview = null;
            byId('class-import-preview').hidden = true;
            byId('class-import-confirm-error').textContent = '';
        });
    }

    async function showAdminPanel(name) {
        document.querySelectorAll('[data-panel]').forEach(panel => { panel.hidden = panel.dataset.panel !== name; });
        document.querySelectorAll('[data-view]').forEach(button => button.classList.toggle('is-active', button.dataset.view === name));
        if (name === 'overview') await loadAdminOverview();
        if (name === 'reports') await loadGradeFolders();
        if (name === 'classes') await loadClassDirectory();
    }

    async function loadAdmin() {
        await loadClassDirectory();
        updateIdentity();
        await showAdminPanel('overview');
    }

    function setupAccountMenu() {
        const toggle = byId('profile-toggle');
        if (!toggle) return;
        toggle.addEventListener('click', () => {
            const menu = byId('profile-menu');
            const expanded = toggle.getAttribute('aria-expanded') === 'true';
            toggle.setAttribute('aria-expanded', String(!expanded));
            menu.hidden = expanded;
        });
        document.addEventListener('click', event => {
            if (!event.target.closest('.account-menu')) {
                byId('profile-menu').hidden = true;
                toggle.setAttribute('aria-expanded', 'false');
            }
        });
        document.querySelector('[data-open-settings]')?.addEventListener('click', async () => {
            byId('profile-menu').hidden = true;
            toggle.setAttribute('aria-expanded', 'false');
            byId('settings-error').textContent = '';
            byId('settings-dialog').showModal();
            if (!isAdmin && byId('class-settings-form')) {
                byId('class-settings-error').textContent = '';
                byId('class-settings-status').textContent = 'Refreshing available class options…';
                try {
                    const directory = await request('/api/classes');
                    classes = directory.grades || [];
                    populateClassSettings();
                } catch (error) {
                    byId('class-settings-status').textContent = 'Class options could not be refreshed.';
                    byId('class-settings-error').textContent = error.message;
                }
            }
        });
        document.querySelector('[data-logout]')?.addEventListener('click', async event => {
            const button = event.currentTarget;
            button.disabled = true;
            try {
                await request('/api/logout', { method: 'POST' });
                window.location.replace('index.html');
            } catch (error) {
                showMessage(error.message, true);
                button.disabled = false;
            }
        });
        document.querySelectorAll('[data-close-dialog]').forEach(button => {
            button.addEventListener('click', () => button.closest('dialog').close());
        });
        document.querySelectorAll('.app-dialog').forEach(dialog => dialog.addEventListener('click', event => {
            if (event.target === dialog) dialog.close();
        }));
    }

    function setupReportActions() {
        document.addEventListener('click', event => {
            if (!event.target.closest('[data-open-report]')) return;
            byId('report-error').textContent = '';
            byId('report-dialog').showModal();
        });

        const imageInput = byId('report-image');
        imageInput?.addEventListener('change', () => {
            const preview = byId('image-preview');
            preview.replaceChildren();
            const file = imageInput.files[0];
            if (!file) return;
            if (!['image/jpeg', 'image/png', 'image/gif', 'image/webp'].includes(file.type) || file.size > 1_048_576) {
                byId('report-error').textContent = 'Choose a JPEG, PNG, GIF, or WebP image smaller than 1 MB.';
                imageInput.value = '';
                return;
            }
            const image = document.createElement('img');
            image.alt = 'Selected maintenance issue photo preview';
            image.src = URL.createObjectURL(file);
            preview.append(image);
            const remove = document.createElement('button');
            remove.type = 'button';
            remove.textContent = 'Remove photo';
            remove.addEventListener('click', () => { imageInput.value = ''; preview.replaceChildren(); });
            preview.append(remove);
        });

        byId('report-form')?.addEventListener('submit', async event => {
            event.preventDefault();
            const form = event.currentTarget;
            const submit = form.querySelector('[type="submit"]');
            const file = imageInput.files[0];
            byId('report-error').textContent = '';
            submit.disabled = true;
            try {
                const image = file ? await new Promise((resolve, reject) => {
                    const reader = new FileReader();
                    reader.addEventListener('load', () => resolve(reader.result));
                    reader.addEventListener('error', () => reject(new Error('The image could not be read.')));
                    reader.readAsDataURL(file);
                }) : null;
                const fields = new FormData(form);
                const result = await request('/api/reports', {
                    method: 'POST',
                    body: JSON.stringify({
                        title: fields.get('title'),
                        location: fields.get('location'),
                        category: fields.get('category'),
                        description: fields.get('description'),
                        image
                    })
                });
                form.reset();
                byId('image-preview').replaceChildren();
                byId('report-dialog').close();
                if (isAdmin) {
                    if (activeFolder === 'grades') await loadGradeFolders();
                    if (activeFolder === 'reports') await loadSectionReports();
                    if (activeFolder === 'unassigned') await loadUnassignedReports();
                    await loadAdminOverview();
                    showMessage(`Report FI-${String(result.id).padStart(6, '0')} created.`);
                } else {
                    reports = await request('/api/reports');
                    renderStudentReports();
                    showMessage(`Report FI-${String(result.id).padStart(6, '0')} submitted. It is now in My Reports.`);
                    await loadStudentNotifications();
                }
            } catch (error) {
                byId('report-error').textContent = error.message;
            } finally {
                submit.disabled = false;
            }
        });
    }

    function setupStudentActions() {
        byId('report-search')?.addEventListener('input', renderStudentReports);
        ['status-filter', 'category-filter', 'sort-filter'].forEach(id => byId(id)?.addEventListener('change', renderStudentReports));
        byId('report-filters')?.addEventListener('submit', event => event.preventDefault());
        byId('clear-filters')?.addEventListener('click', () => {
            ['report-search', 'status-filter', 'category-filter'].forEach(id => { if (byId(id)) byId(id).value = ''; });
            if (byId('sort-filter')) byId('sort-filter').value = 'newest';
            renderStudentReports();
        });
    }

    function setupAdminActions() {
        byId('class-folder-form').addEventListener('submit', submitClassFolderDialog);
        document.querySelector('.class-management-actions')?.addEventListener('click', event => {
            const createGrade = event.target.closest('[data-create-grade]');
            if (createGrade) {
                openClassFolderDialog({ mode: 'create-grade', type: 'grade' });
                return;
            }
            const createSection = event.target.closest('[data-create-section]');
            if (!createSection) return;
            if (!classes.some(grade => grade.isActive)) {
                showMessage('Create or restore a grade level before adding sections.', true);
                return;
            }
            openClassFolderDialog({ mode: 'create-section', type: 'section' });
        });
        document.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => {
            showAdminPanel(button.dataset.view).catch(error => showMessage(error.message, true));
        }));
        document.addEventListener('click', event => {
            const gradeLink = event.target.closest('[data-open-grade]');
            if (!gradeLink) return;
            showAdminPanel('reports').then(async () => {
                const grade = classes.find(item => item.name === gradeLink.dataset.openGrade);
                if (grade) await loadSectionFolders(grade.id);
            }).catch(error => showMessage(error.message, true));
        });
        byId('folder-breadcrumbs').addEventListener('click', event => {
            const button = event.target.closest('[data-folder-level]');
            if (!button) return;
            const action = button.dataset.folderLevel;
            const work = action === 'grades' ? loadGradeFolders() : loadSectionFolders(activeGradeId);
            work.catch(error => showMessage(error.message, true));
        });
        byId('folder-content').addEventListener('click', event => {
            const menuTrigger = event.target.closest('.folder-menu-trigger');
            if (menuTrigger) {
                const menu = menuTrigger.nextElementSibling;
                const opening = menu.hidden;
                byId('folder-content').querySelectorAll('.folder-action-menu').forEach(item => { item.hidden = true; });
                byId('folder-content').querySelectorAll('.folder-menu-trigger').forEach(item => item.setAttribute('aria-expanded', 'false'));
                menu.hidden = !opening;
                menuTrigger.setAttribute('aria-expanded', String(opening));
                return;
            }
            const archivedToggle = event.target.closest('[data-toggle-archived]');
            if (archivedToggle) {
                if (archivedToggle.dataset.toggleArchived === 'grades') {
                    showArchivedGrades = !showArchivedGrades;
                    loadGradeFolders().catch(error => showMessage(error.message, true));
                } else {
                    showArchivedSections = !showArchivedSections;
                    loadSectionFolders(activeGradeId).catch(error => showMessage(error.message, true));
                }
                return;
            }
            const manageButton = event.target.closest('[data-folder-manage]');
            if (manageButton) {
                prepareFolderManagement(manageButton).catch(error => showMessage(error.message, true));
                return;
            }
            const button = event.target.closest('[data-folder-action]');
            if (!button) return;
            const action = button.dataset.folderAction;
            const work = action === 'grade'
                ? (showArchivedSections = false, loadSectionFolders(Number(button.dataset.folderId)))
                : action === 'section'
                    ? (activeSectionId = Number(button.dataset.folderId), loadSectionReports())
                    : loadUnassignedReports();
            work.catch(error => showMessage(error.message, true));
        });
        ['folder-search', 'folder-status', 'folder-category', 'folder-sort'].forEach(id => {
            const input = byId(id);
            input?.addEventListener(input.tagName === 'INPUT' ? 'input' : 'change', () => {
                const work = activeFolder === 'reports'
                    ? loadSectionReports()
                    : activeFolder === 'unassigned'
                        ? loadUnassignedReports()
                        : null;
                work?.catch(error => showMessage(error.message, true));
            });
        });
        byId('folder-clear')?.addEventListener('click', () => {
            byId('folder-search').value = '';
            byId('folder-status').value = '';
            byId('folder-category').value = '';
            byId('folder-sort').value = 'newest';
            const work = activeFolder === 'unassigned' ? loadUnassignedReports() : loadSectionReports();
            work.catch(error => showMessage(error.message, true));
        });
        byId('admin-report-form').addEventListener('submit', async event => {
            event.preventDefault();
            const button = event.currentTarget.querySelector('[type="submit"]');
            button.disabled = true;
            byId('admin-report-error').textContent = '';
            try {
                await request(`/api/reports/${activeReportId}`, {
                    method: 'PATCH',
                    body: JSON.stringify({
                        status: byId('admin-status').value,
                        priority: byId('admin-priority').value,
                        assignee: byId('admin-assignee').value,
                        publicUpdate: byId('admin-public-update').value,
                        adminNotes: byId('admin-notes').value
                    })
                });
                showMessage('Report updates saved.');
                await openReport(activeReportId);
                if (activeFolder === 'reports') await loadSectionReports();
                if (activeFolder === 'unassigned') await loadUnassignedReports();
                await loadAdminOverview();
            } catch (error) {
                byId('admin-report-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('delete-report').addEventListener('click', async () => {
            if (!activeReportId || !window.confirm('Delete this report permanently? This action cannot be undone.')) return;
            const button = byId('delete-report');
            button.disabled = true;
            try {
                await request(`/api/reports/${activeReportId}`, { method: 'DELETE' });
                byId('report-details-dialog').close();
                showMessage('Report deleted.');
                if (activeFolder === 'reports') await loadSectionReports();
                if (activeFolder === 'unassigned') await loadUnassignedReports();
                await loadAdminOverview();
            } catch (error) {
                showMessage(error.message, true);
            } finally {
                button.disabled = false;
            }
        });
    }

    function setupSettings() {
        byId('settings-form')?.addEventListener('submit', async event => {
            event.preventDefault();
            const button = event.currentTarget.querySelector('[type="submit"]');
            button.disabled = true;
            byId('settings-error').textContent = '';
            try {
                const result = await request('/api/profile', {
                    method: 'POST',
                    body: JSON.stringify({ username: byId('settings-name').value.trim() })
                });
                session.username = result.username;
                updateIdentity();
                byId('settings-dialog').close();
                showMessage('Your profile has been updated.');
            } catch (error) {
                byId('settings-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('settings-grade')?.addEventListener('change', event => {
            setSettingsSections(event.target.value);
            byId('class-settings-error').textContent = '';
        });
        byId('class-settings-form')?.addEventListener('submit', async event => {
            event.preventDefault();
            const form = event.currentTarget;
            const button = form.querySelector('[type="submit"]');
            const gradeValue = byId('settings-grade').value;
            const sectionValue = byId('settings-section').value;
            byId('class-settings-error').textContent = '';
            if (Boolean(gradeValue) !== Boolean(sectionValue)) {
                byId('class-settings-error').textContent = 'Choose both a grade level and section, or leave both as Not set.';
                return;
            }
            button.disabled = true;
            try {
                await request('/api/profile/class', {
                    method: 'PATCH',
                    body: JSON.stringify({
                        gradeLevelId: gradeValue ? Number(gradeValue) : null,
                        sectionId: sectionValue ? Number(sectionValue) : null
                    })
                });
                profile.gradeLevelId = gradeValue ? Number(gradeValue) : null;
                profile.sectionId = sectionValue ? Number(sectionValue) : null;
                const grade = classes.find(item => item.id === profile.gradeLevelId);
                profile.gradeLevel = grade?.name || null;
                profile.section = grade?.sections.find(item => item.id === profile.sectionId)?.name || null;
                populateClassSettings();
                updateClassStatus();
                showMessage(grade ? 'Your grade and section were saved.' : 'Your class assignment was cleared.');
            } catch (error) {
                byId('class-settings-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
    }

    async function initialize() {
        document.querySelectorAll('.page-message').forEach(message => { message.hidden = true; });
        try {
            session = await request('/api/me');
            if (!['user', 'admin'].includes(session.role))
                throw new Error('Your account role is not recognized.');
            if (session.role !== role) {
                window.location.replace(session.role === 'admin' ? 'dashboard_admin.html' : 'dashboard_user.html');
                return;
            }
            setupAccountMenu();
            setupSettings();
            setupReportActions();
            if (isAdmin) {
                setupClassOptionsImport();
                setupAdminActions();
                await loadAdmin();
            } else {
                setupStudentActions();
                await loadStudent();
            }
        } catch (error) {
            showMessage(`Could not load your workspace: ${error.message}`, true);
        }
    }

    document.addEventListener('click', async event => {
        const open = event.target.closest('[data-action="open-report"]');
        if (open) {
            await openReport(Number(open.dataset.reportId));
            return;
        }
        const notification = event.target.closest('[data-action="open-notification"]');
        if (notification && !isAdmin) {
            try {
                await request(`/api/notifications/${notification.dataset.notificationId}/read`, { method: 'POST' });
                await openReport(Number(notification.dataset.reportId));
            } catch (error) {
                showMessage(error.message, true);
            }
        }
    });

    if (page === 'admin') byId('admin-global-message').hidden = true;
    initialize();
})();
