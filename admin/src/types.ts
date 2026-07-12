export interface Overview {
  totalUsers: number;
  approvedUsers: number;
  pendingUsers: number;
  activeLast24Hours: number;
  activeLast7Days: number;
  totalMessages: number;
  totalCharacters: number;
}

export interface AdminUser {
  id: string;
  email: string;
  createdAt: string;
  lastActiveAt: string;
  isApproved: boolean;
  isAdmin: boolean;
  messageCount: number;
  characterCount: number;
}

export interface PagedUsers {
  items: AdminUser[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export type ApprovalFilter = 'all' | 'approved' | 'pending';
export type UserSort = 'lastActiveDesc' | 'createdDesc' | 'createdAsc' | 'emailAsc' | 'messagesDesc';
